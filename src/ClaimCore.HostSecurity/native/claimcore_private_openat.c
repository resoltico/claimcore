#define _GNU_SOURCE 1

#include <errno.h>
#include <fcntl.h>
#include <string.h>
#include <stdint.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <unistd.h>

#define CLAIMCORE_PRIVATE_ABI_VERSION 2

static int valid_leaf(const char *leaf) {
    return leaf != NULL && leaf[0] != '\0' && strchr(leaf, '/') == NULL &&
           strcmp(leaf, ".") != 0 && strcmp(leaf, "..") != 0;
}

int cc_private_abi_version(void) {
    return CLAIMCORE_PRIVATE_ABI_VERSION;
}

/* Fixed-width projections keep libc constants and struct stat layout native. */
int cc_private_flags(int *values, int count) {
    if (values == NULL || count != 5) {
        errno = EINVAL;
        return -1;
    }
    values[0] = O_DIRECTORY;
    values[1] = O_NOFOLLOW;
    values[2] = O_CLOEXEC;
    values[3] = O_NONBLOCK;
    values[4] = AT_REMOVEDIR;
    return 0;
}

static void identity_values(const struct stat *value, uint64_t *values) {
    values[0] = (uint64_t)value->st_dev;
    values[1] = (uint64_t)value->st_ino;
    values[2] = (uint64_t)value->st_uid;
    values[3] = (uint64_t)value->st_mode;
    values[4] = (uint64_t)value->st_nlink;
    values[5] = (uint64_t)value->st_size;
}

int cc_private_fstat(int descriptor, uint64_t *values, int count) {
    struct stat value;
    if (values == NULL || count != 6) {
        errno = EINVAL;
        return -1;
    }
    if (fstat(descriptor, &value) != 0) {
        return -1;
    }
    identity_values(&value, values);
    return 0;
}

int cc_private_fstatat(int directory, const char *name, uint64_t *values, int count) {
    struct stat value;
    if (name == NULL || values == NULL || count != 6) {
        errno = EINVAL;
        return -1;
    }
    if (fstatat(directory, name, &value, AT_SYMLINK_NOFOLLOW) != 0) {
        return -1;
    }
    identity_values(&value, values);
    return 0;
}

int cc_openat_create(int parent, const char *leaf) {
    if (!valid_leaf(leaf)) {
        errno = EINVAL;
        return -1;
    }

    return openat(parent, leaf,
                  O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC | O_SYNC,
                  0600);
}

static void remove_created_if_same(int parent, const char *leaf, int descriptor) {
    struct stat opened;
    struct stat current;

    if (fstat(descriptor, &opened) == 0 &&
        fstatat(parent, leaf, &current, AT_SYMLINK_NOFOLLOW) == 0 &&
        opened.st_dev == current.st_dev && opened.st_ino == current.st_ino) {
        unlinkat(parent, leaf, 0);
    }
}

int cc_openat_lock(int parent, const char *leaf, int *created) {
    if (!valid_leaf(leaf) || created == NULL) {
        errno = EINVAL;
        return -1;
    }

    int descriptor = openat(parent, leaf,
                            O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC | O_SYNC | O_NONBLOCK,
                            0600);

    *created = descriptor >= 0;

    if (descriptor < 0 && errno == EEXIST) {
        descriptor = openat(parent, leaf,
                            O_RDWR | O_NOFOLLOW | O_CLOEXEC | O_SYNC | O_NONBLOCK);
    }

    if (descriptor < 0) {
        return -1;
    }

    if (flock(descriptor, LOCK_EX | LOCK_NB) != 0) {
        int failure = errno;
        if (*created) {
            remove_created_if_same(parent, leaf, descriptor);
        }
        close(descriptor);
        errno = failure;
        return -1;
    }

    return descriptor;
}
