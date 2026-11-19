# The desktop set to the smallest display mode that holds a KSP window of the given size (WIDTHxHEIGHT). KSP in
# batchmode on Windows draws no bigger than the desktop, and a GitHub runner's starts at 1024x768. Used by the setup
# action; anything it cannot do it says and leaves.
param([string]$Window = '1920x1080')

Add-Type @'
using System.Runtime.InteropServices;
public static class Desktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
}
'@
function Mode([int]$i) { $dm = New-Object Desktop+DEVMODE; $dm.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($dm); if ([Desktop]::EnumDisplaySettings([NullString]::Value, $i, [ref]$dm)) { $dm } }
$cur = Mode -1
"desktop $($cur.dmPelsWidth)x$($cur.dmPelsHeight)"
$modes = for ($i = 0; ($m = Mode $i); $i++) { $m }
"modes: " + (($modes | ForEach-Object { "$($_.dmPelsWidth)x$($_.dmPelsHeight)" } | Select-Object -Unique) -join ' ')
$w, $h = $Window -split 'x' | ForEach-Object { [int]$_ }
$fit = $modes | Where-Object { $_.dmPelsWidth -ge $w -and $_.dmPelsHeight -ge $h } |
    Sort-Object { $_.dmPelsWidth * $_.dmPelsHeight } | Select-Object -First 1
if (-not $fit) { "no mode holds ${w}x${h}"; exit 0 }
$cur.dmPelsWidth = $fit.dmPelsWidth; $cur.dmPelsHeight = $fit.dmPelsHeight; $cur.dmFields = 0x80000 -bor 0x100000
"setting $($fit.dmPelsWidth)x$($fit.dmPelsHeight)"
"ChangeDisplaySettings: $([Desktop]::ChangeDisplaySettings([ref]$cur, 0)) (0 is success)"
$now = Mode -1
"desktop now $($now.dmPelsWidth)x$($now.dmPelsHeight)"
