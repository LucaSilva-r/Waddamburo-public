// Horizon user exception -> Linux-style signal handler (shim.c lx_exception).
// Replaces libnx's __libnx_exception_entry. The kernel enters on the faulting thread with a tiny stack,
// x0 = exception desc, x1 = saved frame {x0..x8 @0, lr @72, sp @80, elr @88, pstate @96, esr @108, far @112};
// x9..x29 and q0..q31 still hold the faulting values, x2..x8 are free (saved in the frame).
// Entry stores the whole context as an lx_regs on the faulting thread's own stack, then resumes the
// thread (svcReturnFromException) in `tramp`, which calls lx_exception and jumps to the redirected
// context. x16 is clobbered by the final jump (IP0: never live across the runtime's redirect targets).
// If lx_exception declines, g_exc_fatal makes the re-fault go to the kernel (crash report at the real PC).
//
// lx_regs: x[0..30] @0, sp @248, pc @256, pstate @264, far @272, esr @280, desc @288,
//          v[0..31] @304, fpsr @816, fpcr @820. size 832.

    .section .text.__libnx_exception_entry, "ax", %progbits
    .global __libnx_exception_entry
    .type __libnx_exception_entry, %function
    .balign 16
__libnx_exception_entry:
    cbz x1, 9f
    adrp x2, g_exc_fatal
    ldr w2, [x2, #:lo12:g_exc_fatal]
    cbnz w2, 9f
    ldr x2, [x1, #80]               // faulting sp
    sub x2, x2, #(1024 + 832)       // margin below the interrupted frame
    and x2, x2, #~15
    stp x9, x10, [x2, #72]
    stp x11, x12, [x2, #88]
    stp x13, x14, [x2, #104]
    stp x15, x16, [x2, #120]
    stp x17, x18, [x2, #136]
    stp x19, x20, [x2, #152]
    stp x21, x22, [x2, #168]
    stp x23, x24, [x2, #184]
    stp x25, x26, [x2, #200]
    stp x27, x28, [x2, #216]
    str x29, [x2, #232]
    ldp x3, x4, [x1, #0]
    stp x3, x4, [x2, #0]
    ldp x3, x4, [x1, #16]
    stp x3, x4, [x2, #16]
    ldp x3, x4, [x1, #32]
    stp x3, x4, [x2, #32]
    ldp x3, x4, [x1, #48]
    stp x3, x4, [x2, #48]
    ldr x3, [x1, #64]
    str x3, [x2, #64]
    ldr x3, [x1, #72]               // lr
    str x3, [x2, #240]
    ldp x3, x4, [x1, #80]           // sp, pc
    stp x3, x4, [x2, #248]
    ldr w3, [x1, #96]               // pstate
    str x3, [x2, #264]
    ldr x3, [x1, #112]              // far
    str x3, [x2, #272]
    ldr w3, [x1, #108]              // esr
    str x3, [x2, #280]
    str x0, [x2, #288]              // desc
    add x3, x2, #304
    stp q0, q1, [x3, #0]
    stp q2, q3, [x3, #32]
    stp q4, q5, [x3, #64]
    stp q6, q7, [x3, #96]
    stp q8, q9, [x3, #128]
    stp q10, q11, [x3, #160]
    stp q12, q13, [x3, #192]
    stp q14, q15, [x3, #224]
    stp q16, q17, [x3, #256]
    stp q18, q19, [x3, #288]
    stp q20, q21, [x3, #320]
    stp q22, q23, [x3, #352]
    stp q24, q25, [x3, #384]
    stp q26, q27, [x3, #416]
    stp q28, q29, [x3, #448]
    stp q30, q31, [x3, #480]
    mrs x3, fpsr
    str w3, [x2, #816]
    mrs x3, fpcr
    str w3, [x2, #820]
    // resume the thread in tramp with sp = x0 = lx_regs
    str x2, [x1, #0]
    str x2, [x1, #80]
    adr x3, tramp
    str x3, [x1, #88]
    mov w0, wzr
    svc 0x28                        // svcReturnFromException(0)
    b .
9:  mov w0, #0xf801
    svc 0x28
    b .

tramp:                              // faulting thread, sp = x0 = lx_regs (callee-preserved across the call)
    mov x1, x0
    ldr x0, [x1, #288]
    bl lx_exception
    mov x17, sp                     // lx_regs
    add x16, x17, #304
    ldp q0, q1, [x16, #0]
    ldp q2, q3, [x16, #32]
    ldp q4, q5, [x16, #64]
    ldp q6, q7, [x16, #96]
    ldp q8, q9, [x16, #128]
    ldp q10, q11, [x16, #160]
    ldp q12, q13, [x16, #192]
    ldp q14, q15, [x16, #224]
    ldp q16, q17, [x16, #256]
    ldp q18, q19, [x16, #288]
    ldp q20, q21, [x16, #320]
    ldp q22, q23, [x16, #352]
    ldp q24, q25, [x16, #384]
    ldp q26, q27, [x16, #416]
    ldp q28, q29, [x16, #448]
    ldp q30, q31, [x16, #480]
    ldr w16, [x17, #816]
    msr fpsr, x16
    ldr w16, [x17, #820]
    msr fpcr, x16
    ldr x16, [x17, #264]
    msr nzcv, x16
    ldp x0, x1, [x17, #0]
    ldp x2, x3, [x17, #16]
    ldp x4, x5, [x17, #32]
    ldp x6, x7, [x17, #48]
    ldp x8, x9, [x17, #64]
    ldp x10, x11, [x17, #80]
    ldp x12, x13, [x17, #96]
    ldp x14, x15, [x17, #112]
    ldr x18, [x17, #144]
    ldp x19, x20, [x17, #152]
    ldp x21, x22, [x17, #168]
    ldp x23, x24, [x17, #184]
    ldp x25, x26, [x17, #200]
    ldp x27, x28, [x17, #216]
    ldp x29, x30, [x17, #232]
    ldr x16, [x17, #248]
    mov sp, x16
    ldr x16, [x17, #256]
    ldr x17, [x17, #136]
    br x16
    .size __libnx_exception_entry, . - __libnx_exception_entry
