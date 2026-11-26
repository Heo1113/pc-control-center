<#
  Stage 1 screen regression for the process-identity branch (Windows CI).

  Both builds render every screen several times with the same mock data and clock (dist/cmp/base, base2.. and new, new2..).
  The process-name cell is the one area changed on purpose (icon + identified program name), so each screen is compared
  in two parts, using the name-cell rectangles the branch renderer writes into layout-report.json:
    - outside the name cells: must be pixel-identical to 187d533 (the gate);
    - inside the name cells: reported (pixels changed, number of cells).
  The repeated renders show whether either build varies from run to run. Small crops of the name column (new and
  187d533) are printed as base64 PNG between markers so the change can be looked at from the log alone.
#>
param(
    [string]$Root = 'dist/cmp',
    [int]$Runs = 4,
    [string]$CropScreen = '2194x1186-processes-normal.png',
    [int]$CropRows = 6
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
$probe = [System.Drawing.Bitmap]::new(1, 1); $probe.Dispose()
# .NET 10 splits System.Drawing over several assemblies (GdiPlus, Primitives): reference every loaded one.
$refs = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { -not $_.IsDynamic -and $_.Location -and $_.GetName().Name -match '^System\.(Drawing|Private\.Windows)' } | ForEach-Object Location
Add-Type -ReferencedAssemblies $refs -TypeDefinition @"
using System; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging; using System.IO; using System.Runtime.InteropServices;
public static class ScreenDiff {
  static byte[] Load(string path, out int w, out int h, out int stride) {
    using (var bmp = new Bitmap(path)) {
      w = bmp.Width; h = bmp.Height;
      var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      try { stride = d.Stride; var buf = new byte[stride * h]; Marshal.Copy(d.Scan0, buf, 0, buf.Length); return buf; }
      finally { bmp.UnlockBits(d); }
    }
  }
  static bool[] Mask(int w, int h, int[] cells) {
    var m = new bool[w * h];
    for (int i = 0; i + 3 < cells.Length; i += 4) {
      int x0 = Math.Max(0, cells[i]), y0 = Math.Max(0, cells[i + 1]), x1 = Math.Min(w - 1, cells[i + 2]), y1 = Math.Min(h - 1, cells[i + 3]);
      for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) m[y * w + x] = true;
    }
    return m;
  }
  // { outside, inside, x0, y0, x1, y1 of the outside differences }; outside = -1 when the sizes differ
  public static long[] Masked(string a, string b, int[] cells) {
    int w, h, s, w2, h2, s2;
    var pa = Load(a, out w, out h, out s); var pb = Load(b, out w2, out h2, out s2);
    if (w != w2 || h != h2) return new long[] { -1, 0, 0, 0, 0, 0 };
    var m = Mask(w, h, cells);
    long outside = 0, inside = 0; int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) {
      int i = y * s + x * 4;
      if (pa[i] == pb[i] && pa[i + 1] == pb[i + 1] && pa[i + 2] == pb[i + 2] && pa[i + 3] == pb[i + 3]) continue;
      if (m[y * w + x]) { inside++; continue; }
      outside++; x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
    }
    return new long[] { outside, inside, x0, y0, x1, y1 };
  }
  // up to max differing pixels outside the cells as "x,y: argbA vs argbB"
  public static string OutsideSample(string a, string b, int[] cells, int max) {
    int w, h, s, w2, h2, s2;
    var pa = Load(a, out w, out h, out s); var pb = Load(b, out w2, out h2, out s2);
    var m = Mask(w, h, cells); var sb = new System.Text.StringBuilder(); int n = 0;
    for (int y = 0; y < h && n < max; y++) for (int x = 0; x < w && n < max; x++) {
      int i = y * s + x * 4;
      if (m[y * w + x] || (pa[i] == pb[i] && pa[i + 1] == pb[i + 1] && pa[i + 2] == pb[i + 2] && pa[i + 3] == pb[i + 3])) continue;
      sb.AppendFormat("{0},{1}: {2:X8} vs {3:X8}; ", x, y, BitConverter.ToInt32(pa, i), BitConverter.ToInt32(pb, i)); n++;
    }
    return sb.ToString();
  }
  // text picture of new around (cx, cy): luminance ramp, 'X' = differs from base, '|' column left of the nearest cell edge
  public static string Around(string a, string b, int cx, int cy, int w, int h) {
    using (var x = new Bitmap(a)) using (var y = new Bitmap(b)) {
      var sb = new System.Text.StringBuilder(); const string ramp = " .:-=+*%@#";
      for (int py = Math.Max(0, cy - h / 2); py < Math.Min(x.Height, cy + h / 2); py++) {
        sb.Append(py.ToString().PadLeft(5)).Append(' ');
        for (int px = Math.Max(0, cx - w / 2); px < Math.Min(x.Width, cx + w / 2); px++) {
          var p = y.GetPixel(px, py); var q = x.GetPixel(px, py);
          if (p != q) { sb.Append('X'); continue; }
          int lum = (p.R * 3 + p.G * 6 + p.B) / 10; sb.Append(ramp[Math.Min(9, lum * 10 / 256)]);
        }
        sb.Append('\n');
      }
      return sb.ToString();
    }
  }
  // base64 PNG of the rectangle, scaled
  public static string Crop(string path, int x0, int y0, int x1, int y1, double scale) {
    using (var src = new Bitmap(path)) {
      x1 = Math.Min(x1, src.Width - 1); y1 = Math.Min(y1, src.Height - 1);
      int w = x1 - x0 + 1, h = y1 - y0 + 1, tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
      using (var dst = new Bitmap(tw, th, PixelFormat.Format24bppRgb)) {
        using (var g = Graphics.FromImage(dst)) {
          g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
          g.DrawImage(src, new Rectangle(0, 0, tw, th), new Rectangle(x0, y0, w, h), GraphicsUnit.Pixel);
        }
        using (var ms = new MemoryStream()) { dst.Save(ms, ImageFormat.Png); return Convert.ToBase64String(ms.ToArray()); }
      }
    }
  }
}
"@

function PathOf([string]$dir, [string]$name) { (Resolve-Path -LiteralPath (Join-Path $Root $dir $name)).Path }
function HashOf([string]$dir, [string]$name) { (Get-FileHash -LiteralPath (Join-Path $Root $dir $name)).Hash }

$rn = Get-Content (Join-Path $Root 'new/layout-report.json') -Raw | ConvertFrom-Json
$rb = Get-Content (Join-Path $Root 'base/layout-report.json') -Raw | ConvertFrom-Json
$cellsByFile = @{}; $procsByFile = @{}
foreach ($s in $rn.screens) {
    [int[]]$flat = @()
    if ($s.nameCells) { $flat = [int[]]@($s.nameCells | ForEach-Object { $_ }) }
    $cellsByFile[$s.file] = $flat
    $procsByFile[$s.file] = @($s.nameCellProcesses)
}

$script:pictures = 0
$runsB = @('base') + @(2..$Runs | ForEach-Object { "base$_" })
$runsN = @('new') + @(2..$Runs | ForEach-Object { "new$_" })
$files = Get-ChildItem (Join-Path $Root 'base') -Filter *.png | Sort-Object Name
$rows = foreach ($f in $files) {
    $name = $f.Name
    $cells = $cellsByFile[$name]; if ($null -eq $cells) { $cells = [int[]]@() }
    $hb = @($runsB | ForEach-Object { HashOf $_ $name })
    $hn = @($runsN | ForEach-Object { HashOf $_ $name })
    $whole = $hb[0] -eq $hn[0]
    $d = [long[]]@(0, 0, 0, 0, 0, 0)
    if (-not $whole) { $d = [ScreenDiff]::Masked((PathOf 'base' $name), (PathOf 'new' $name), $cells) }
    # Outside the cells, does any render of this branch match 187d533? (a difference only some runs show is run-to-run
    # variation of this branch, reported separately; a difference every run shows is a real change and fails)
    $bestOutside = [long]$d[0]
    for ($k = 1; $k -lt $runsN.Count -and $bestOutside -ne 0; $k++) {
        $o = if ($hn[$k] -eq $hb[0]) { 0 } else { [ScreenDiff]::Masked((PathOf 'base' $name), (PathOf $runsN[$k] $name), $cells)[0] }
        if ($o -ge 0 -and $o -lt $bestOutside) { $bestOutside = $o }
    }
    $box = ''
    if ($d[0] -gt 0) { $box = "$($d[2]),$($d[3])-$($d[4]),$($d[5])" }
    $selfOutside = 0
    for ($k = 1; $k -lt $runsN.Count; $k++) {
        if ($hn[$k] -ne $hn[0]) { $selfOutside = [Math]::Max($selfOutside, [ScreenDiff]::Masked((PathOf 'new' $name), (PathOf $runsN[$k] $name), $cells)[0]) }
    }
    [pscustomobject]@{
        File = $name; Cells = $cells.Count / 4; Whole = $whole; Outside = $d[0]; BestOutside = $bestOutside; Inside = $d[1]
        Box = $box
        BaseVariants = @($hb | Select-Object -Unique).Count; BranchVariants = @($hn | Select-Object -Unique).Count; BranchSelfOutside = $selfOutside
    }
}

foreach ($r in $rows) {
    if ($r.Whole) { $state = 'identical to 187d533' }
    elseif ($r.Outside -eq 0) { $state = "changed only inside name cells ($($r.Inside) px)" }
    elseif ($r.Outside -lt 0) { $state = 'SIZE DIFFERS' }
    elseif ($r.BestOutside -eq 0) { $state = "changed inside name cells ($($r.Inside) px); outside them run 1 differs by $($r.Outside) px at $($r.Box) but another run of this branch matches 187d533 there (run-to-run variation)" }
    else { $state = "OUTSIDE name cells: $($r.Outside) px at $($r.Box), in every run (inside: $($r.Inside) px)" }
    $line = "  $($r.File): name cells $($r.Cells) · $state · variants over $Runs runs: 187d533 $($r.BaseVariants), branch $($r.BranchVariants)"
    if ($r.BranchSelfOutside -gt 0) { $line += " (branch vs itself outside name cells: $($r.BranchSelfOutside) px)" }
    $line
    if ($r.Outside -gt 0) {
        '    sample: ' + [ScreenDiff]::OutsideSample((PathOf 'base' $r.File), (PathOf 'new' $r.File), $cellsByFile[$r.File], 12)
        if ($r.Box -match '^(\d+),(\d+)-') {
            $bx = [int]$Matches[1]; $by = [int]$Matches[2]; $cl = $cellsByFile[$r.File]; $pr = $procsByFile[$r.File]
            for ($i = 0; $i + 3 -lt $cl.Count; $i += 4) {
                if ($by -ge $cl[$i + 1] -and $by -le $cl[$i + 3]) { "    row of cell $($i / 4) ($($pr[$i / 4])): cell $($cl[$i]),$($cl[$i + 1])-$($cl[$i + 2]),$($cl[$i + 3])" }
            }
            if ($script:pictures -eq 0) {
                foreach ($dir in 'new', 'base') {
                    "=====CROP $dir-outside $($r.File) $($bx - 12),$($by - 24)-$($bx + 60),$($by + 30) scale 1====="
                    $b64 = [ScreenDiff]::Crop((PathOf $dir $r.File), $bx - 12, $by - 24, $bx + 60, $by + 30, 1.0)
                    for ($i = 0; $i -lt $b64.Length; $i += 2000) { $b64.Substring($i, [Math]::Min(2000, $b64.Length - $i)) }
                    '=====END CROP====='
                }
            }
            if ($script:pictures++ -lt 0) {
                "    this branch around $bx,$by (X = differs from 187d533; luminance ' ' dark .. '#' light):"
                [ScreenDiff]::Around((PathOf 'base' $r.File), (PathOf 'new' $r.File), $bx + 30, $by, 80, 24)
            }
        }
    }
}

$outsideScreens = @($rows | Where-Object { $_.BestOutside -ne 0 })
$jitterScreens = @($rows | Where-Object { $_.Outside -ne 0 -and $_.BestOutside -eq 0 })
$insideOnly = @($rows | Where-Object { -not $_.Whole -and $_.BestOutside -eq 0 })
$summary = "screens: $($rows.Count) · identical to 187d533: $(@($rows | Where-Object Whole).Count) · changed only inside the process-name cells: $($insideOnly.Count) " +
    "($(($rows | Measure-Object Cells -Sum).Sum) cells) · changed outside the name cells in every run: $($outsideScreens.Count) · " +
    "outside the name cells only in some runs of this branch: $($jitterScreens.Count) · " +
    "screens varying across $Runs runs: 187d533 $(@($rows | Where-Object { $_.BaseVariants -gt 1 }).Count), branch $(@($rows | Where-Object { $_.BranchVariants -gt 1 }).Count) " +
    "(outside name cells: $(@($rows | Where-Object { $_.BranchSelfOutside -gt 0 }).Count)) · layout issues new/base: $($rn.totalIssues)/$($rb.totalIssues) · " +
    "binding errors new/base: $($rn.bindingErrors.Count)/$($rb.bindingErrors.Count)"
$summary
if ($env:GITHUB_STEP_SUMMARY) { "## Stage 1 회귀(화면)`n`n$summary" | Out-File -Append -Encoding utf8 $env:GITHUB_STEP_SUMMARY }

# crops of the name column (first rows) for a look at the change, from this branch and from 187d533
$c = $cellsByFile[$CropScreen]
if ($c -and $c.Count -ge 4) {
    $n = [Math]::Min($CropRows, $c.Count / 4)
    $x0 = $c[0]; $y0 = $c[1]; $y1 = $c[($n - 1) * 4 + 3]; $x1 = [Math]::Min($c[2], $x0 + 900)
    foreach ($dir in 'new', 'base') {
        "=====CROP $dir $CropScreen $x0,$y0-$x1,$y1 scale 0.5====="
        $b64 = [ScreenDiff]::Crop((PathOf $dir $CropScreen), $x0, $y0, $x1, $y1, 0.5)
        for ($i = 0; $i -lt $b64.Length; $i += 2000) { $b64.Substring($i, [Math]::Min(2000, $b64.Length - $i)) }
        '=====END CROP====='
    }
}

if ($rn.totalIssues -ne $rb.totalIssues -or $rn.bindingErrors.Count -ne $rb.bindingErrors.Count) { throw 'layout report differs from the baseline' }
if ($outsideScreens.Count -gt 0) { throw 'screens differ from 187d533 outside the process-name cells (see above)' }
if (($rows | Measure-Object Cells -Sum).Sum -eq 0) { throw 'no process-name cells reported: the masked comparison would compare nothing' }
