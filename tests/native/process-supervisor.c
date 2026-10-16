#define _POSIX_C_SOURCE 200809L
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <stdint.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

/* Resolution follows the supported runtime's mode/effective-identity PATH search.
   It deliberately does not substitute ACL-aware access() for that search oracle. */
static char *joined(const char *directory, const char *name) {
    size_t length = strlen(directory), suffix = strlen(name);
    if (length > SIZE_MAX - suffix - 2) return NULL;
    char *path = malloc(length + suffix + 2);
    if (path == NULL) return NULL;
    memcpy(path, directory, length);
    if (length > 0 && path[length - 1] != '/') path[length++] = '/';
    memcpy(path + length, name, suffix + 1);
    return path;
}

static int group_member(gid_t group) {
    if (group == getegid()) return 1;
    int count = getgroups(0, NULL);
    if (count < 0) return 0;
    gid_t *groups = count == 0 ? NULL : malloc((size_t)count * sizeof(gid_t));
    if (count != 0 && groups == NULL) return -1;
    int received = getgroups(count, groups);
    int result = 0;
    for (int index = 0; index < received; index++) {
        if (groups[index] == group) result = 1;
    }
    free(groups);
    return result;
}

static int executable_candidate(const char *path) {
    struct stat file;
    if (stat(path, &file) != 0 || S_ISDIR(file.st_mode)) return 0;
    mode_t executable = file.st_mode & (S_IXUSR | S_IXGRP | S_IXOTH);
    if (executable == (S_IXUSR | S_IXGRP | S_IXOTH)) return 1;
    if (executable == 0) return 0;
    uid_t user = geteuid();
    if (user == 0) return 1;
    if (user == file.st_uid) return (executable & S_IXUSR) != 0;
    bool group = (executable & S_IXGRP) != 0, other = (executable & S_IXOTH) != 0;
    if (group == other) return group;
    int member = group_member(file.st_gid);
    return member < 0 ? -1 : member ? group : other;
}

static int output_path(const char *path) {
    size_t count = strlen(path), offset = 0;
    /* Both qualified kernels bound an exec pathname to at most 4096 bytes. */
    if (count == 0 || count > 4096) return 2;
    while (offset < count) {
        ssize_t written = write(STDOUT_FILENO, path + offset, count - offset);
        if (written < 0 && errno == EINTR) continue;
        if (written <= 0) return 2;
        offset += (size_t)written;
    }
    return 0;
}

static int existing_candidate(const char *directory, const char *name) {
    char *path = joined(directory, name);
    if (path == NULL) return 2;
    struct stat file;
    int result = stat(path, &file) == 0 && !S_ISDIR(file.st_mode) ? output_path(path) : 1;
    free(path);
    return result;
}

static char *current_directory(void) {
    size_t count = 256;
    for (;;) {
        char *directory = malloc(count);
        if (directory == NULL) return NULL;
        if (getcwd(directory, count) != NULL) return directory;
        int error = errno;
        free(directory);
        if (error != ERANGE || count > SIZE_MAX / 2) return NULL;
        count *= 2;
    }
}

static int path_candidate(const char *component, size_t length, const char *name) {
    char *directory = malloc(length + 1);
    if (directory == NULL) return 2;
    memcpy(directory, component, length);
    directory[length] = '\0';
    char *candidate = joined(directory, name);
    free(directory);
    if (candidate == NULL) return 2;
    int admitted = executable_candidate(candidate);
    int result = admitted > 0 ? output_path(candidate) : admitted < 0 ? 2 : 1;
    free(candidate);
    return result;
}

static int search_path(const char *name) {
    const char *path = getenv("PATH");
    if (path == NULL) return 1;
    while (*path != '\0') {
        const char *end = strchr(path, ':');
        size_t length = end == NULL ? strlen(path) : (size_t)(end - path);
        if (length != 0) {
            int result = path_candidate(path, length, name);
            if (result != 1) return result;
        }
        if (end == NULL) break;
        path = end + 1;
    }
    return 1;
}

static int resolve(const char *name, const char *executable_directory) {
    if (name[0] == '/') return output_path(name);
    if (executable_directory[0] != '\0') {
        int result = existing_candidate(executable_directory, name);
        if (result != 1) return result;
    }
    char *directory = current_directory();
    if (directory == NULL) return 2;
    int result = existing_candidate(directory, name);
    free(directory);
    return result != 1 ? result : search_path(name);
}

/* Test-only lifetime anchor. Protocol: S, E <exit>, K <signal>, F, T. */
static long long milliseconds(void) {
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) return -1;
    return (long long)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

static bool number(const char *text, long *result) {
    char *end = NULL;
    errno = 0;
    *result = strtol(text, &end, 10);
    return errno == 0 && text[0] != '\0' && *end == '\0' && *result > 0 && *result <= INT_MAX;
}

static bool send_status(int descriptor, const char *tag, int value) {
    char bytes[32];
    int count = value < 0 ? snprintf(bytes, sizeof(bytes), "%s\n", tag)
                          : snprintf(bytes, sizeof(bytes), "%s %d\n", tag, value);
    ssize_t written;
    do { written = write(descriptor, bytes, (size_t)count); } while (written < 0 && errno == EINTR);
    return written == count;
}

static int observe(pid_t group, int timeout) {
    long long begin = milliseconds();
    if (begin < 0) return 2;
    for (;;) {
        if (kill(-group, 0) != 0) return errno == ESRCH ? 0 : 2;
        long long now = milliseconds();
        if (now < 0 || now - begin >= timeout) return 1;
        struct timespec pause = {0, 10000000};
        while (nanosleep(&pause, &pause) != 0 && errno == EINTR) { }
    }
}

static void teardown(pid_t child, int status_fd) {
    /* SIGTERM is ignored only by the anchor; the exec child restores it. */
    (void)kill(-getpgrp(), SIGTERM);
    long long begin = milliseconds();
    int status;
    while (waitpid(child, &status, WNOHANG) == 0) {
        long long now = milliseconds();
        if (begin < 0 || now < 0 || now - begin >= 100) break;
        struct timespec pause = {0, 1000000};
        (void)nanosleep(&pause, NULL);
    }
    (void)send_status(status_fd, "T", -1);
    /* This final group signal is issued while its leader is still alive. */
    (void)kill(-getpgrp(), SIGKILL);
    _exit(125);
}

static _Noreturn void report_target_failure(int error_write) {
    int failure = errno;
    ssize_t written;
    do {
        written = write(error_write, &failure, sizeof(failure));
    } while (written < 0 && errno == EINTR);
    _exit(126);
}

static void target(int control, int status, int error_write, const char *file, char **arguments,
                   const struct sigaction *original_pipe, const struct sigaction *original_term) {
    close(control);
    close(status);
    if (sigaction(SIGPIPE, original_pipe, NULL) != 0 || sigaction(SIGTERM, original_term, NULL) != 0) {
        report_target_failure(error_write);
    }
    execv(file, arguments);
    report_target_failure(error_write);
}

static bool exited_status(pid_t child, int descriptor, bool *finished) {
    int status;
    pid_t result = waitpid(child, &status, WNOHANG);
    if (result == 0 || (result < 0 && errno == EINTR)) return true;
    if (result < 0) return false;
    *finished = true;
    if (WIFEXITED(status)) return send_status(descriptor, "E", WEXITSTATUS(status));
    if (WIFSIGNALED(status)) return send_status(descriptor, "K", WTERMSIG(status));
    return false;
}

static bool confirm_exec(int control, int status_fd, pid_t child, int exec_error) {
    for (;;) {
        struct pollfd channels[2] = {{control, POLLIN, 0}, {exec_error, POLLIN, 0}};
        int ready = poll(channels, 2, -1);
        if (ready < 0 && errno == EINTR) continue;
        if (ready < 0 || channels[0].revents != 0) teardown(child, status_fd);
        if (channels[1].revents != 0) {
            int error;
            ssize_t received;
            do { received = read(exec_error, &error, sizeof(error)); }
            while (received < 0 && errno == EINTR);
            return received == 0;
        }
    }
}

static bool controller_open(int control) {
    struct pollfd channel = {control, POLLIN, 0};
    int ready = poll(&channel, 1, 10);
    if (ready < 0) return errno == EINTR;
    if (ready == 0) return true;
    char byte;
    ssize_t count = read(control, &byte, 1);
    /* EOF or any command byte requests teardown; no open-ended command grammar. */
    return count < 0 && errno == EINTR;
}

static void supervise(int control, int status_fd, pid_t child, int exec_error) {
    close(STDIN_FILENO);
    close(STDOUT_FILENO);
    close(STDERR_FILENO);
    bool executed = confirm_exec(control, status_fd, child, exec_error);
    close(exec_error);
    if (!executed || !send_status(status_fd, "S", -1)) {
        (void)send_status(status_fd, "F", -1);
        teardown(child, status_fd);
    }
    bool finished = false;
    for (;;) {
        if (!finished && !exited_status(child, status_fd, &finished)) teardown(child, status_fd);
        if (!controller_open(control)) teardown(child, status_fd);
    }
}

static bool anchor(int control, int status_fd, struct sigaction *original_pipe,
                   struct sigaction *original_term) {
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = SIG_IGN;
    sigemptyset(&action.sa_mask);
    return sigaction(SIGPIPE, &action, original_pipe) == 0
        && sigaction(SIGTERM, &action, original_term) == 0
        && fcntl(control, F_SETFD, FD_CLOEXEC) == 0
        && fcntl(status_fd, F_SETFD, FD_CLOEXEC) == 0 && setsid() >= 0;
}

static int start_failure(int status_fd) {
    (void)send_status(status_fd, "F", -1);
    return 124;
}

static int run_supervisor(int control, int status_fd, char **arguments) {
    struct sigaction original_pipe, original_term;
    if (!anchor(control, status_fd, &original_pipe, &original_term)) return start_failure(status_fd);
    int errors[2];
    if (pipe(errors) != 0) return start_failure(status_fd);
    if (fcntl(errors[1], F_SETFD, FD_CLOEXEC) != 0) {
        close(errors[0]); close(errors[1]); return start_failure(status_fd);
    }
    pid_t child = fork();
    if (child < 0) {
        close(errors[0]); close(errors[1]); return start_failure(status_fd);
    }
    if (child == 0) {
        close(errors[0]);
        target(control, status_fd, errors[1], arguments[0], &arguments[1], &original_pipe, &original_term);
    }
    close(errors[1]);
    supervise(control, status_fd, child, errors[0]);
    return 124;
}

static bool launch_arguments(int argc, char **argv, int *control, int *status) {
    long first, second;
    if (argc < 6) return false;
    if (!number(argv[1], &first) || !number(argv[2], &second) || first == second
        || first <= STDERR_FILENO || second <= STDERR_FILENO || strcmp(argv[3], "--") != 0) return false;
    *control = (int)first;
    *status = (int)second;
    return true;
}

static int observe_arguments(char **argv) {
    long group, timeout;
    if (!number(argv[2], &group) || group <= 1 || !number(argv[3], &timeout)) return 2;
    return observe((pid_t)group, (int)timeout);
}

int main(int argc, char **argv) {
    if (argc == 4 && strcmp(argv[1], "--resolve") == 0) return resolve(argv[2], argv[3]);
    if (argc == 4 && strcmp(argv[1], "--observe") == 0) return observe_arguments(argv);
    int control, status;
    if (!launch_arguments(argc, argv, &control, &status)) return 124;
    return run_supervisor(control, status, &argv[4]);
}
