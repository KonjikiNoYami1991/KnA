using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace KnA.Core.Tools;

/// <summary>
/// The processor's instruction-set level (x86-64 psABI: v1 baseline, v2 SSE4.2/POPCNT, v3 AVX2/FMA/BMI, v4 AVX-512).
/// .NET's JIT uses every level by itself; the encoders are told when they need it (x265 leaves AVX-512 off unless
/// asked: "asm=avx512"). SVT-AV1, dav1d and FFmpeg pick AVX2/AVX-512 at run time.
/// </summary>
public static class CpuFeatures
{
    public static int X86Level { get; } = Detect();

    private static int Detect()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return 0;
        if (Avx512F.IsSupported && Avx512F.VL.IsSupported && Avx512BW.IsSupported && Avx512CD.IsSupported && Avx512DQ.IsSupported
            && Avx2.IsSupported && Fma.IsSupported && Bmi1.IsSupported && Bmi2.IsSupported && Lzcnt.IsSupported) return 4;
        if (Avx2.IsSupported && Fma.IsSupported && Bmi1.IsSupported && Bmi2.IsSupported && Lzcnt.IsSupported) return 3;
        if (Sse42.IsSupported && Popcnt.IsSupported) return 2;
        return 1;
    }

    /// <summary>"x86-64-v4 (AVX-512)", "ARM64"…</summary>
    public static string Description => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => X86Level switch
        {
            4 => "x86-64-v4 (AVX-512)",
            3 => "x86-64-v3 (AVX2)",
            2 => "x86-64-v2 (SSE4.2)",
            _ => "x86-64",
        },
        Architecture.Arm64 => "ARM64",
        var a => a.ToString(),
    };
}
