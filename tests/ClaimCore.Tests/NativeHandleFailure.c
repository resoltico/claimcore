#define cc_private_fstat cc_real_private_fstat
#include "claimcore_private_openat.c"
#undef cc_private_fstat

/* Inject only a regular-file metadata failure; kernel locking remains real. */
int cc_private_fstat(int descriptor, uint64_t *values, int count) {
    struct stat value;
    if (fstat(descriptor, &value) == 0 && S_ISREG(value.st_mode)) {
        errno = EIO;
        return -1;
    }
    return cc_real_private_fstat(descriptor, values, count);
}

int cc_probe_lock(const char *path) {
    int descriptor = open(path, O_RDWR | O_NOFOLLOW | O_CLOEXEC);
    if (descriptor < 0) return -2;
    int result = flock(descriptor, LOCK_EX | LOCK_NB);
    close(descriptor);
    return result;
}
