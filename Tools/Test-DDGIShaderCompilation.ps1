$ErrorActionPreference = 'Stop'

# Exercise the SM5 compiler as well as DXC: DXC alone misses Unity's divergent-barrier error.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class DDGIShaderCompilerCheck
{
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int D3DCompile(byte[] source, UIntPtr length, string sourceName,
        IntPtr defines, IntPtr include, string entryPoint, string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr GetPointer(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate UIntPtr GetSize(IntPtr self);

    static string ReadBlob(IntPtr blob)
    {
        if (blob == IntPtr.Zero) return "";
        IntPtr table = Marshal.ReadIntPtr(blob);
        var pointer = Marshal.GetDelegateForFunctionPointer<GetPointer>(
            Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
        var size = Marshal.GetDelegateForFunctionPointer<GetSize>(
            Marshal.ReadIntPtr(table, 4 * IntPtr.Size));
        return Marshal.PtrToStringAnsi(pointer(blob), checked((int)size(blob).ToUInt64()));
    }

    public static void Compile(string source, string name, string entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        IntPtr code, errors;
        int result = D3DCompile(bytes, (UIntPtr)bytes.Length, name,
            IntPtr.Zero, IntPtr.Zero, entry, "cs_5_0", 1u << 11, 0, out code, out errors);
        try
        {
            if (result < 0) throw new Exception(entry + ": " + ReadBlob(errors));
        }
        finally
        {
            if (code != IntPtr.Zero) Marshal.Release(code);
            if (errors != IntPtr.Zero) Marshal.Release(errors);
        }
    }
}
'@

$root = Split-Path $PSScriptRoot -Parent
$path = Join-Path $root 'Assets/DDGI/Shaders/DDGIProbeBlend.compute'
$state = Get-Content -Raw (Join-Path $root 'Assets/DDGI/Shaders/DDGIProbeState.hlsl')
$source = (Get-Content -Raw $path).Replace('#include "DDGIProbeState.hlsl"', $state)
$invalidSource = $source.Replace(
    'bool shouldUpdate = (probeState.y & DDGI_PROBE_UPDATE) != 0;',
    'if ((probeState.y & DDGI_PROBE_UPDATE) == 0) return; bool shouldUpdate = true;')
$rejectedUnsafeBarrier = $false
try {
    [DDGIShaderCompilerCheck]::Compile($invalidSource, $path, 'BlendIrradiance')
} catch {
    if ($_.Exception.Message -notmatch 'sync') { throw }
    $rejectedUnsafeBarrier = $true
}
if (-not $rejectedUnsafeBarrier) { throw 'Compiler did not reject the divergent-barrier regression.' }
Write-Output 'PASS regression control: unsafe early-return/barrier was rejected.'
foreach ($kernel in @('PrepareProbeStates', 'ClassifyProbes', 'BlendIrradiance', 'BlendDistanceMoments')) {
    [DDGIShaderCompilerCheck]::Compile($source, $path, $kernel)
    Write-Output "PASS SM5: $kernel"
}
Write-Output 'All four ProbeBlend/classification kernels compiled with D3DCompile (cs_5_0).'
