namespace WinDiag.Analysis;

/// <summary>
/// Small, exact statistics for event counts. Used to say whether a change in frequency is larger than chance would
/// explain — never to claim a cause.
/// </summary>
public static class RateStatistics
{
    /// <summary>
    /// Two-sided p-value of the conditional (exact binomial) test that two Poisson rates are equal, given
    /// <paramref name="k1"/> events in exposure <paramref name="t1"/> and <paramref name="k2"/> in <paramref name="t2"/>.
    /// Returns null when there is nothing to test.
    /// </summary>
    public static double? RateRatioPValue(int k1, double t1, int k2, double t2)
    {
        var n = k1 + k2;
        if (n == 0 || t1 <= 0 || t2 <= 0)
        {
            return null;
        }

        var p = t1 / (t1 + t2);
        return BinomialTwoSided(k1, n, p);
    }

    /// <summary>Two-sided exact binomial p-value (sum of outcomes no more likely than the observed one).</summary>
    public static double BinomialTwoSided(int k, int n, double p)
    {
        if (n <= 0)
        {
            return 1;
        }

        if (p <= 0)
        {
            return k == 0 ? 1 : 0;
        }

        if (p >= 1)
        {
            return k == n ? 1 : 0;
        }

        if (n > 20_000)
        {
            // Normal approximation with continuity correction for very large counts.
            var mean = n * p;
            var sd = Math.Sqrt(n * p * (1 - p));
            var z = (Math.Abs(k - mean) - 0.5) / sd;
            return Math.Clamp(2 * (1 - NormalCdf(Math.Max(0, z))), 0, 1);
        }

        var observed = LogBinomialPmf(k, n, p);
        var total = 0.0;
        for (var i = 0; i <= n; i++)
        {
            var lp = LogBinomialPmf(i, n, p);
            if (lp <= observed + 1e-7)
            {
                total += Math.Exp(lp);
            }
        }

        return Math.Clamp(total, 0, 1);
    }

    /// <summary>P(X ≥ k) for X ~ Poisson(lambda).</summary>
    public static double PoissonUpperTail(int k, double lambda)
    {
        if (k <= 0)
        {
            return 1;
        }

        if (lambda <= 0)
        {
            return 0;
        }

        var below = 0.0;
        for (var i = 0; i < k; i++)
        {
            below += Math.Exp(i * Math.Log(lambda) - lambda - LogFactorial(i));
        }

        return Math.Clamp(1 - below, 0, 1);
    }

    public static double LogBinomialPmf(int k, int n, double p)
        => LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k) + k * Math.Log(p) + (n - k) * Math.Log(1 - p);

    public static double LogFactorial(int n) => n < 2 ? 0 : LogGamma(n + 1.0);

    /// <summary>Lanczos approximation (g = 7, n = 9), accurate to about 15 significant digits for x &gt; 0.</summary>
    public static double LogGamma(double x)
    {
        ReadOnlySpan<double> c =
        [
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313, -176.61502916214059,
            12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
        ];
        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        }

        x -= 1;
        var a = c[0];
        var t = x + 7.5;
        for (var i = 1; i < 9; i++)
        {
            a += c[i] / (x + i);
        }

        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    public static double NormalCdf(double z)
    {
        // Abramowitz-Stegun 7.1.26 erf approximation (error below 1.5e-7).
        var t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z / 2);
        return z >= 0 ? 0.5 * (1 + y) : 0.5 * (1 - y);
    }

    /// <summary>A short Korean reading of a p-value that avoids overclaiming.</summary>
    public static string Assess(int k1, int k2, double? p)
    {
        if (k1 + k2 < 4 || p is null)
        {
            return "표본이 적어 판단 어려움";
        }

        return p < 0.01 ? "우연으로 보기 어려운 차이 (p<0.01)"
            : p < 0.05 ? "우연으로 보기 어려운 차이 (p<0.05)"
            : "우연으로도 설명 가능한 차이";
    }
}
