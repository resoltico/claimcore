#ifndef CLAIMCORE_PRIVATE_FILE_H
#define CLAIMCORE_PRIVATE_FILE_H

/* Stable scalar results; no platform layouts cross the managed boundary. */
enum claimcore_private_file_status {
    CC_PRIVATE_ADMITTED = 0,
    CC_PRIVATE_MISSING = 1,
    CC_PRIVATE_LINK = 2,
    CC_PRIVATE_NOT_REGULAR = 3,
    CC_PRIVATE_WRONG_OWNER = 4,
    CC_PRIVATE_PERMISSIVE = 5,
    CC_PRIVATE_UNREADABLE = 6,
    CC_PRIVATE_ACL_REFUSED = 7
};

/* On success the caller owns *admitted_fd; every refusal leaves it at -1. */
__attribute__((visibility("default")))
int claimcore_private_file_open(const char *path, int *admitted_fd);

#endif
