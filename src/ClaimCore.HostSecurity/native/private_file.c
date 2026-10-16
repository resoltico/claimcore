#ifndef __APPLE__
#define _POSIX_C_SOURCE 200809L
#endif
#include "private_file.h"
#include <errno.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#ifdef __APPLE__
#include <membership.h>
#include <stdbool.h>
#include <string.h>
#include <sys/acl.h>
#include <uuid/uuid.h>
#endif

static int owner_status(uid_t owner, uid_t effective) {
    return owner == effective ? CC_PRIVATE_ADMITTED : CC_PRIVATE_WRONG_OWNER;
}

#ifdef __APPLE__
static int entry_properties(acl_entry_t entry, acl_tag_t *tag,
                            acl_permset_mask_t *permissions, int *inherit_only) {
    acl_flagset_t flags;
    if (acl_get_tag_type(entry, tag) != 0 ||
        (*tag != ACL_EXTENDED_ALLOW && *tag != ACL_EXTENDED_DENY) ||
        acl_get_flagset_np(entry, &flags) != 0 ||
        acl_get_permset_mask_np(entry, permissions) != 0)
        return 0;
    *inherit_only = acl_get_flag_np(flags, ACL_ENTRY_ONLY_INHERIT);
    return *inherit_only == 0 || *inherit_only == 1;
}

static int allowed_entry(acl_entry_t entry, uid_t owner, uuid_t owner_uuid,
                         bool *owner_mapped) {
    acl_tag_t tag;
    acl_permset_mask_t permissions;
    int inherit_only;
    if (!entry_properties(entry, &tag, &permissions, &inherit_only))
        return 0;
    if (inherit_only == 1 || tag == ACL_EXTENDED_DENY || permissions == 0)
        return 1;
    if (!*owner_mapped) {
        if (mbr_uid_to_uuid(owner, owner_uuid) != 0)
            return 0;
        *owner_mapped = true;
    }
    void *qualifier = acl_get_qualifier(entry);
    if (qualifier == NULL)
        return 0;
    int is_owner = memcmp(qualifier, owner_uuid, sizeof(uuid_t)) == 0;
    acl_free(qualifier);
    return is_owner;
}

static int populated_security(filesec_t security, const struct stat *status) {
    uid_t owner;
    gid_t group;
    mode_t mode;
    return filesec_get_property(security, FILESEC_OWNER, &owner) == 0 &&
        filesec_get_property(security, FILESEC_GROUP, &group) == 0 &&
        filesec_get_property(security, FILESEC_MODE, &mode) == 0 &&
        owner == status->st_uid && group == status->st_gid && mode == status->st_mode;
}

static acl_t descriptor_acl(int fd, int *absent) {
    *absent = 0;
    filesec_t security = filesec_init();
    if (security == NULL)
        return NULL;
    struct stat status;
    int present;
    acl_t acl = NULL;
    if (fstatx_np(fd, &status, security) == 0 && populated_security(security, &status) &&
        filesec_query_property(security, FILESEC_ACL, &present) == 0) {
        if (present == 0) {
            *absent = 1;
        } else if (filesec_get_property(security, FILESEC_ACL, &acl) != 0) {
            if (acl != NULL)
                acl_free(acl);
            acl = NULL;
        }
    }
    filesec_free(security);
    return acl;
}

static int private_acl(int fd, uid_t owner) {
    int absent;
    acl_t acl = descriptor_acl(fd, &absent);
    if (acl == NULL)
        return absent;
    bool owner_mapped = false;
    uuid_t owner_uuid;
    int admitted = 0;
    for (int index = 0; index <= ACL_MAX_ENTRIES; ++index) {
        acl_entry_t entry;
        errno = 0;
        int result = acl_get_entry(acl, index == 0 ? ACL_FIRST_ENTRY : ACL_NEXT_ENTRY,
                                   &entry);
        if (result == -1) {
            admitted = errno == EINVAL;
            break;
        }
        if (result != 0 || index == ACL_MAX_ENTRIES ||
            !allowed_entry(entry, owner, owner_uuid, &owner_mapped))
            break;
    }
    acl_free(acl);
    return admitted;
}
#else
static int private_acl(int fd, uid_t owner) {
    /* POSIX ACL_MASK is reflected in the group-class mode checked by fstat. */
    (void)fd;
    (void)owner;
    return 1;
}
#endif

static int descriptor_status(int fd) {
    struct stat status;
    if (fstat(fd, &status) != 0)
        return CC_PRIVATE_UNREADABLE;
    if (!S_ISREG(status.st_mode))
        return CC_PRIVATE_NOT_REGULAR;
    int ownership = owner_status(status.st_uid, geteuid());
    if (ownership != CC_PRIVATE_ADMITTED)
        return ownership;
    if ((status.st_mode & (S_IRWXG | S_IRWXO)) != 0)
        return CC_PRIVATE_PERMISSIVE;
    if ((status.st_mode & S_IRUSR) == 0)
        return CC_PRIVATE_UNREADABLE;
    return private_acl(fd, status.st_uid) ? CC_PRIVATE_ADMITTED : CC_PRIVATE_ACL_REFUSED;
}

int claimcore_private_file_open(const char *path, int *admitted_fd) {
    if (admitted_fd == NULL)
        return CC_PRIVATE_UNREADABLE;
    *admitted_fd = -1;
    if (path == NULL)
        return CC_PRIVATE_UNREADABLE;
    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
    if (fd == -1) {
        if (errno == ENOENT || errno == ENOTDIR)
            return CC_PRIVATE_MISSING;
        return errno == ELOOP ? CC_PRIVATE_LINK : CC_PRIVATE_UNREADABLE;
    }
    int status = descriptor_status(fd);
    if (status != CC_PRIVATE_ADMITTED) {
        close(fd);
        return status;
    }
    *admitted_fd = fd;
    return CC_PRIVATE_ADMITTED;
}
