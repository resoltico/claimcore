#ifndef __APPLE__
#define _POSIX_C_SOURCE 200809L
#endif
#include "../../src/ClaimCore.HostSecurity/native/private_file.c"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char temporary_path[] = "/tmp/claimcore-private-control-XXXXXX";
static const char *fixture_path = temporary_path;
static int fixture_created = 0;

static void cleanup_fixture(void) {
    if (fixture_created)
        unlink(fixture_path);
}

static void require(int condition, const char *reason) {
    if (!condition) {
        fprintf(stderr, "Private-file control failed: %s\n", reason);
        exit(1);
    }
}

static void check_open(const char *path, int expected) {
    int fd = -9;
    int status = claimcore_private_file_open(path, &fd);
    require(status == expected, "typed admission status");
    if (status == CC_PRIVATE_ADMITTED) {
        require(fd >= 0, "admitted descriptor");
        require((fcntl(fd, F_GETFD) & FD_CLOEXEC) != 0, "close-on-exec");
        require((fcntl(fd, F_GETFL) & O_NONBLOCK) != 0, "nonblocking");
        require(close(fd) == 0, "admitted descriptor closes");
        require(fcntl(fd, F_GETFD) == -1 && errno == EBADF, "descriptor closed");
    } else {
        require(fd == -1, "refusal transfers no descriptor");
    }
}

static void owner_controls(void) {
    require(owner_status((uid_t)42, (uid_t)42) == CC_PRIVATE_ADMITTED, "equal owner");
    require(owner_status((uid_t)42, (uid_t)43) == CC_PRIVATE_WRONG_OWNER, "foreign owner");
    require(owner_status((uid_t)42, (uid_t)0) == CC_PRIVATE_WRONG_OWNER, "no root exception");
    require(descriptor_status(-1) == CC_PRIVATE_UNREADABLE, "failed descriptor inspection");
}

static void descriptor_controls(const char *path, int original) {
    require(write(original, "a", 1) == 1, "synthetic bytes");
    require(fchmod(original, S_IRUSR | S_IWUSR) == 0, "owner-private mode");
    check_open(path, CC_PRIVATE_ADMITTED);
    int admitted = -1;
    require(claimcore_private_file_open(path, &admitted) == CC_PRIVATE_ADMITTED,
            "same-descriptor admission");
    require(unlink(path) == 0, "remove original pathname");
    int replacement = open(path, O_CREAT | O_EXCL | O_WRONLY, S_IRUSR | S_IWUSR);
    require(replacement >= 0, "replacement pathname");
    require(write(replacement, "b", 1) == 1 && close(replacement) == 0,
            "replacement bytes");
    char value = 0;
    require(read(admitted, &value, 1) == 1 && value == 'a', "read admitted inode");
    require(close(admitted) == 0, "same-descriptor cleanup");
}

static void foreign_owner_control(const char *path) {
    /* This real fixture is qualified only with an effective root identity. */
    if (geteuid() != 0)
        return;
    int fd = open(path, O_RDWR);
    require(fd >= 0, "open foreign-owner fixture");
    require(fchown(fd, (uid_t)1, (gid_t)-1) == 0, "assign synthetic foreign owner");
    check_open(path, CC_PRIVATE_WRONG_OWNER);
    require(fchown(fd, geteuid(), (gid_t)-1) == 0, "restore owned fixture");
    require(close(fd) == 0, "close foreign-owner fixture");
}

static void refusal_cleanup(const char *path) {
    int fixture = open(path, O_RDONLY);
    require(fixture >= 0, "open refusal fixture");
    int available = dup(fixture);
    require(available >= 0 && close(available) == 0, "baseline descriptor slot");
    require(fchmod(fixture, S_IRUSR | S_IRGRP) == 0, "permissive fixture");
    for (int index = 0; index < 128; ++index)
        check_open(path, CC_PRIVATE_PERMISSIVE);
    int next = dup(fixture);
    require(next == available, "refusal closes native descriptor");
    require(close(next) == 0, "close cleanup probe");
    require(fchmod(fixture, S_IRUSR | S_IWUSR) == 0, "restore private fixture");
    require(close(fixture) == 0, "close refusal fixture");
}

#ifdef __APPLE__
static void set_entry(int fd, acl_tag_t tag, int inherit_only, int permission, int owner) {
    acl_t acl = acl_init(1);
    require(acl != NULL, "create ACL");
    acl_entry_t entry;
    require(acl_create_entry(&acl, &entry) == 0, "create entry");
    require(acl_set_tag_type(entry, tag) == 0, "set tag");
    uuid_t qualifier;
    require(mbr_uid_to_uuid(geteuid(), qualifier) == 0, "owner UUID fixture");
    if (!owner)
        qualifier[0] ^= 0x80;
    require(acl_set_qualifier(entry, qualifier) == 0, "set qualifier");
    acl_permset_t permissions;
    require(acl_get_permset(entry, &permissions) == 0, "get permissions");
    require(acl_clear_perms(permissions) == 0, "clear permissions");
    if (permission)
        require(acl_add_perm(permissions, (acl_perm_t)permission) == 0, "add permission");
    if (inherit_only) {
        acl_flagset_t flags;
        require(acl_get_flagset_np(entry, &flags) == 0, "get flags");
        require(acl_add_flag_np(flags, ACL_ENTRY_ONLY_INHERIT) == 0, "inherit-only flag");
    }
    require(acl_set_fd_np(fd, acl, ACL_TYPE_EXTENDED) == 0, "apply owned ACL");
    acl_free(acl);
}

static void filesec_controls(void) {
    filesec_t security = filesec_init();
    require(security != NULL, "create filesec control");
    struct stat status = {0};
    status.st_uid = geteuid();
    status.st_gid = getegid();
    status.st_mode = S_IFREG | S_IRUSR | S_IWUSR;
    require(!populated_security(security, &status), "unpopulated filesec refuses");
    require(filesec_set_property(security, FILESEC_OWNER, &status.st_uid) == 0,
            "set filesec owner");
    require(!populated_security(security, &status), "owner-only filesec refuses");
    require(filesec_set_property(security, FILESEC_GROUP, &status.st_gid) == 0,
            "set filesec group");
    require(!populated_security(security, &status), "missing mode refuses");
    require(filesec_set_property(security, FILESEC_MODE, &status.st_mode) == 0,
            "set filesec mode");
    require(populated_security(security, &status), "matching filesec admitted");
    ++status.st_uid;
    require(!populated_security(security, &status), "mismatched owner refuses");
    --status.st_uid;
    ++status.st_gid;
    require(!populated_security(security, &status), "mismatched group refuses");
    --status.st_gid;
    status.st_mode ^= S_IWUSR;
    require(!populated_security(security, &status), "mismatched mode refuses");
    filesec_free(security);
}

static void acl_controls(const char *path) {
    filesec_controls();
    int fd = open(path, O_RDWR);
    require(fd >= 0, "open ACL fixture");
    acl_t empty = acl_init(0);
    require(empty != NULL && acl_set_fd_np(fd, empty, ACL_TYPE_EXTENDED) == 0,
            "apply empty ACL");
    acl_free(empty);
    check_open(path, CC_PRIVATE_ADMITTED);
    set_entry(fd, ACL_EXTENDED_DENY, 0, ACL_READ_DATA, 0);
    check_open(path, CC_PRIVATE_ADMITTED);
    set_entry(fd, ACL_EXTENDED_ALLOW, 0, ACL_READ_DATA, 1);
    check_open(path, CC_PRIVATE_ADMITTED);
    set_entry(fd, ACL_EXTENDED_ALLOW, 1, ACL_READ_DATA, 0);
    check_open(path, CC_PRIVATE_ADMITTED);
    set_entry(fd, ACL_EXTENDED_ALLOW, 0, 0, 0);
    check_open(path, CC_PRIVATE_ADMITTED);
    set_entry(fd, ACL_EXTENDED_ALLOW, 0, ACL_READ_DATA, 0);
    check_open(path, CC_PRIVATE_ACL_REFUSED);
    acl_t invalid = acl_init(1);
    acl_entry_t invalid_entry = NULL;
    require(invalid != NULL && acl_create_entry(&invalid, &invalid_entry) == 0,
            "create undefined ACL tag");
    bool mapped = false;
    uuid_t owner_uuid;
    require(!allowed_entry(invalid_entry, geteuid(), owner_uuid, &mapped),
            "undefined ACL tag refuses");
    require(!mapped, "undefined tag does not invoke membership");
    require(!allowed_entry(NULL, geteuid(), owner_uuid, &mapped),
            "failed ACL field extraction refuses");
    acl_free(invalid);
    set_entry(fd, ACL_EXTENDED_ALLOW, 0, ACL_WRITE_SECURITY, 0);
    check_open(path, CC_PRIVATE_ACL_REFUSED);
    set_entry(fd, ACL_EXTENDED_ALLOW, 0, ACL_CHANGE_OWNER, 0);
    check_open(path, CC_PRIVATE_ACL_REFUSED);
    /* A failed ACL read is refused rather than treated as an empty ACL. */
    require(private_acl(-1, geteuid()) == 0, "invalid descriptor ACL refuses");
    require(close(fd) == 0, "close ACL fixture");
}
#else
static void acl_controls(const char *path) {
    /* The Linux POSIX ACL mask boundary is exercised by the managed setfacl fixture. */
    check_open(path, CC_PRIVATE_ADMITTED);
}
#endif

int main(int argc, char **argv) {
    require(argc == 1 || argc == 2, "fixture argument count");
    if (argc == 2)
        fixture_path = argv[1];
    owner_controls();
    require(atexit(cleanup_fixture) == 0, "register owned cleanup");
    int fd = argc == 1 ? mkstemp(temporary_path) :
        open(fixture_path, O_CREAT | O_EXCL | O_RDWR, S_IRUSR | S_IWUSR);
    const char *path = fixture_path;
    fixture_created = fd >= 0;
    require(fd >= 0, "owned temporary file");
    descriptor_controls(path, fd);
    require(close(fd) == 0, "close unlinked original");
    foreign_owner_control(path);
    refusal_cleanup(path);
    acl_controls(path);
    require(unlink(path) == 0, "remove owned fixture");
    fixture_created = 0;
    puts("PRIVATE_FILE_CONTROLS_OK");
    return 0;
}
