/* Sandbox (gVisor) compatibility shim for the Unity editor, preloaded by unity.sh.
   - gVisor rejects priority-inheritance futex unlocks (EPERM): make PI mutexes normal mutexes.
   - gVisor reports SCHED_FIFO/RR priority range as 0..0, which makes FMOD audio init fail (error 33):
     report the Linux range 1..99 and accept real-time scheduling requests as no-ops.
   Build: see the end of this file */
int pthread_mutexattr_setprotocol(void *attr, int protocol) { (void)attr; (void)protocol; return 0; }
int sched_get_priority_min(int policy) { return (policy == 1 || policy == 2) ? 1 : 0; }
int sched_get_priority_max(int policy) { return (policy == 1 || policy == 2) ? 99 : 0; }
int pthread_attr_setschedpolicy(void *attr, int policy) { (void)attr; (void)policy; return 0; }
int pthread_attr_setschedparam(void *attr, const void *param) { (void)attr; (void)param; return 0; }
int pthread_attr_setinheritsched(void *attr, int inherit) { (void)attr; (void)inherit; return 0; }
int pthread_setschedparam(unsigned long th, int policy, const void *param) { (void)th; (void)policy; (void)param; return 0; }

/* - gVisor rejects arch_prctl(ARCH_SET_GS) with EINVAL. Unity's shader compiler (UnityShaderCompiler, PE loader for
     D3DCompiler_47.dll in PESetupFS) needs a GS base for the Windows TEB and crashed at gs:0x30. The CPU allows the
     FSGSBASE instructions in user space, so set / read the GS base directly and forward every other syscall.
   Build: gcc -shared -fPIC -nostdlib -O2 -mfsgsbase -o nopi.so nopi.c */
#ifndef NOPI_NO_GS
typedef __builtin_va_list va_list;
extern int *__errno_location(void);
static long raw_syscall6(long n, long a, long b, long c, long d, long e, long f)
{
    long ret;
    register long r10 __asm__("r10") = d;
    register long r8 __asm__("r8") = e;
    register long r9 __asm__("r9") = f;
    __asm__ volatile("syscall" : "=a"(ret) : "a"(n), "D"(a), "S"(b), "d"(c), "r"(r10), "r"(r8), "r"(r9) : "rcx", "r11", "memory");
    return ret;
}
long syscall(long n, ...)
{
    va_list ap; __builtin_va_start(ap, n);
    long a = __builtin_va_arg(ap, long), b = __builtin_va_arg(ap, long), c = __builtin_va_arg(ap, long);
    long d = __builtin_va_arg(ap, long), e = __builtin_va_arg(ap, long), f = __builtin_va_arg(ap, long);
    __builtin_va_end(ap);
    if (n == 158 /* SYS_arch_prctl */) {
        if (a == 0x1001 /* ARCH_SET_GS */) { __asm__ volatile("wrgsbase %0" :: "r"(b) : "memory"); return 0; }
        if (a == 0x1004 /* ARCH_GET_GS */) { unsigned long v; __asm__ volatile("rdgsbase %0" : "=r"(v)); *(unsigned long *)b = v; return 0; }
    }
    long r = raw_syscall6(n, a, b, c, d, e, f);
    if (r < 0 && r > -4096) { *__errno_location() = (int)-r; return -1; }
    return r;
}
#endif
