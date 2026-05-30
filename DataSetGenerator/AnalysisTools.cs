using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace DataSetGenerator
{
    public static class AnalysisTools
    {

        public static class Smooth
        {

            // Wrapper
            public static double[] SmoothEmaZeroPhaseTime(double[] times, double[] values, double tauSec)
            {
                if (times == null)
                    throw new ArgumentNullException(nameof(times));

                if (values == null)
                    throw new ArgumentNullException(nameof(values));

                if (times.Length != values.Length)
                    throw new ArgumentException("times and values must have the same length");

                var points = new List<IntensityPoint>(times.Length);

                for (int i = 0; i < times.Length; i++)
                {
                    points.Add(new IntensityPoint(times[i], values[i]));
                }

                var smoothed = SmoothEmaZeroPhaseTime(points, tauSec);

                return smoothed
                    .Select(p => p.Y)
                    .ToArray();
            }


            // 1) Exponential Moving Average (EMA) smoother
            // alpha in [0,1]; higher alpha = less smoothing (more weight on the latest sample)
            public static List<IntensityPoint> SmoothEma(List<IntensityPoint> src, double alpha)
            {
                var result = new List<IntensityPoint>(src?.Count ?? 0);
                if (src == null || src.Count == 0) return result;

                alpha = Math.Max(0.0, Math.Min(1.0, alpha));

                double acc = src[0].Y; // warm-start with first value
                result.Add(new IntensityPoint(src[0].X, acc));

                for (int i = 1; i < src.Count; i++)
                {
                    acc = alpha * src[i].Y + (1.0 - alpha) * acc;
                    result.Add(new IntensityPoint(src[i].X, acc));
                }
                return result;
            }

            public static List<IntensityPoint> SmoothEmaZeroPhaseTime(List<IntensityPoint> src, double tauSec)
            {
                var n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0 || tauSec <= 0) return new List<IntensityPoint>(src ?? new List<IntensityPoint>());

                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];

                for (int i = 0; i < n; i++) { t[i] = src[i].X; y[i] = src[i].Y; tx[i] = src[i].TargetX; ty[i] = src[i].TargetY; }

                // forward EMA: y_f[i] = a_i*y[i] + (1-a_i)*y_f[i-1], a_i = 1-exp(-dt/tau)
                var yf = new double[n];
                yf[0] = y[0];
                for (int i = 1; i < n; i++)
                {
                    double dt = Math.Max(0.0, t[i] - t[i - 1]);
                    double a = 1.0 - Math.Exp(-(dt / tauSec));
                    yf[i] = a * y[i] + (1.0 - a) * yf[i - 1];
                }

                // backward EMA: apply same on reversed series to remove phase lag
                var yb = new double[n];
                yb[n - 1] = yf[n - 1];
                for (int i = n - 2; i >= 0; i--)
                {
                    double dt = Math.Max(0.0, t[i + 1] - t[i]);
                    double a = 1.0 - Math.Exp(-(dt / tauSec));
                    yb[i] = a * yf[i] + (1.0 - a) * yb[i + 1];
                }

                for (int i = 0; i < n; i++) res.Add(new IntensityPoint(t[i], yb[i], tx[i], ty[i]));
                return res;
            }

            public static List<IntensityPoint> SmoothCenteredMovingAverage(List<IntensityPoint> src, double windowSec)
            {
                var n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0 || windowSec <= 0) return new List<IntensityPoint>(src ?? new List<IntensityPoint>());

                int L = 0, R = 0;
                double sum = 0.0; int count = 0;

                for (int i = 0; i < n; i++)
                {
                    double center = src[i].X;
                    double left = center - windowSec / 2.0;
                    double right = center + windowSec / 2.0;

                    // expand right bound
                    while (R < n && src[R].X <= right) { sum += src[R].Y; R++; count++; }
                    // shrink left bound
                    while (L < R && src[L].X < left) { sum -= src[L].Y; L++; count--; }

                    double avg = (count > 0) ? (sum / count) : src[i].Y;
                    res.Add(new IntensityPoint(src[i].X, avg));
                }
                return res;
            }

            // 2) Add Gaussian noise (mean 0, stdDev in same units as Y). Optional clamping.
            public static List<IntensityPoint> AddNoiseGaussian(
                List<IntensityPoint> src,
                double stdDev,
                int? seed = null,
                double? clampMin = null,
                double? clampMax = null)
            {
                var result = new List<IntensityPoint>(src?.Count ?? 0);
                if (src == null || src.Count == 0 || stdDev <= 0) return new List<IntensityPoint>(src ?? new List<IntensityPoint>());

                var rng = seed.HasValue ? new Random(seed.Value) : new Random();

                // Box–Muller transform
                double NextGaussian()
                {
                    double u1 = 1.0 - rng.NextDouble(); // (0,1]
                    double u2 = 1.0 - rng.NextDouble();
                    return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                }

                foreach (var p in src)
                {
                    double y = p.Y + stdDev * NextGaussian();

                    if (clampMin.HasValue && y < clampMin.Value) y = clampMin.Value;
                    if (clampMax.HasValue && y > clampMax.Value) y = clampMax.Value;

                    result.Add(new IntensityPoint(p.X, y, p.TargetX, p.TargetY));
                }
                return result;
            }


        }

        public static class SmartSmoother
        {
            // dt: seconds between samples (e.g., 1.0 if 1 Hz)
            // globalTau: ~30s defines "global" trend horizon
            // localTau:  ~3-5s defines "local" behavior
            // aMin/aMax: bounds for adaptive EMA gain (smaller = heavier smoothing)
            // medianWin: local median window to kill spikes when local>>global
            // leash:     optional clamp distance from trend (0 = disabled)
            public static List<IntensityPoint> SmoothSmart(
                List<IntensityPoint> src,
                double globalTau = 30.0,   // seconds: defines slow trend horizon
                double localTau = 4.0,    // seconds: defines local horizon
                double aMin = 0.02,        // min EMA gain (heaviest smoothing)
                double aMax = 0.6,         // max EMA gain (lightest smoothing)
                int medianWin = 5,         // local median window (to suppress spikes)
                double leash = 0.0         // optional clamp distance from global trend (0 = disabled)
            )
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                // Extract arrays
                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];

                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                // EMA helper
                double emaGain(double tau, double dt) => dt / Math.Max(dt, tau);

                var yOut = new double[n];
                yOut[0] = y[0];

                double trend = y[0], trendPrev = y[0];
                double fast = y[0], fastPrev = y[0];
                double yPrev = y[0];

                var dq = new LinkedList<double>();

                void pushMedian(double v)
                {
                    dq.AddLast(v);
                    if (dq.Count > medianWin) dq.RemoveFirst();
                }
                double median()
                {
                    var arr = dq.ToList();
                    arr.Sort();
                    int m = arr.Count;
                    if (m == 0) return y[0];
                    return (m % 2 == 1) ? arr[m / 2] : 0.5 * (arr[m / 2 - 1] + arr[m / 2]);
                }

                pushMedian(y[0]);

                const double eps = 1e-9;
                const double spikeRatioForMedian = 3.0; // when to replace input with median

                for (int i = 1; i < n; i++)
                {
                    double dt = Math.Max(1e-9, t[i] - t[i - 1]);

                    // Update EMAs
                    double ag = emaGain(globalTau, dt);
                    double al = emaGain(localTau, dt);

                    trendPrev = trend;
                    fastPrev = fast;
                    trend = trend + ag * (y[i] - trend);
                    fast = fast + al * (y[i] - fast);

                    // Slopes
                    double globalSlope = Math.Abs((trend - trendPrev) / dt);
                    double localSlope = Math.Abs((fast - fastPrev) / dt);

                    // Ratio → adaptive EMA gain
                    double ratio = localSlope / (globalSlope + eps);
                    double aEff = aMax / (1.0 + ratio);
                    if (aEff < aMin) aEff = aMin;
                    if (aEff > aMax) aEff = aMax;

                    // Optional median replacement on spikes
                    pushMedian(y[i]);
                    double xEff = (ratio > spikeRatioForMedian) ? median() : y[i];

                    // Adaptive EMA
                    double yi = yPrev + aEff * (xEff - yPrev);

                    // Optional leash to global trend
                    if (leash > 0.0)
                    {
                        double lo = trend - leash;
                        double hi = trend + leash;
                        if (yi < lo) yi = lo;
                        if (yi > hi) yi = hi;
                    }

                    yOut[i] = yi;
                    yPrev = yi;
                }

                // Reassemble IntensityPoints
                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }

            public static List<IntensityPoint> SmoothSmartZeroPhase(
    List<IntensityPoint> src,
    double globalTau = 30.0,   // seconds for slow slope detection
    double localTau = 5.0     // seconds for local wiggle suppression
)
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                // Extract arrays
                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];

                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                // First pass: slow trend (global)
                var trend = new double[n];
                trend[0] = y[0];
                for (int i = 1; i < n; i++)
                {
                    double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                    double a = 1.0 - Math.Exp(-(dt / globalTau));
                    trend[i] = a * y[i] + (1 - a) * trend[i - 1];
                }
                // Backward pass to remove lag
                for (int i = n - 2; i >= 0; i--)
                {
                    double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                    double a = 1.0 - Math.Exp(-(dt / globalTau));
                    trend[i] = a * trend[i] + (1 - a) * trend[i + 1];
                }

                // Second pass: local smoother to kill wiggles around trend
                var ySmooth = new double[n];
                ySmooth[0] = trend[0];
                for (int i = 1; i < n; i++)
                {
                    double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                    double a = 1.0 - Math.Exp(-(dt / localTau));
                    ySmooth[i] = a * trend[i] + (1 - a) * ySmooth[i - 1];
                }
                for (int i = n - 2; i >= 0; i--)
                {
                    double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                    double a = 1.0 - Math.Exp(-(dt / localTau));
                    ySmooth[i] = a * ySmooth[i] + (1 - a) * ySmooth[i + 1];
                }

                // Rebuild IntensityPoints
                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], ySmooth[i], tx[i], ty[i]));

                return res;
            }

            public static List<IntensityPoint> SmoothEdgeAware(
    List<IntensityPoint> src,
    double tauFlat = 10.0,   // heavy smoothing when slope is small
    double tauEdge = 2.0,    // light smoothing when slope is large
    double slopeThresh = 0.1 // threshold: % per sec (adjust for your data)
)
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                // Extract arrays
                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                var yOut = new double[n];
                yOut[0] = y[0];

                // Forward pass
                for (int i = 1; i < n; i++)
                {
                    double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                    double slope = Math.Abs((y[i] - y[i - 1]) / dt);

                    // If slope < threshold → heavy smoothing
                    // If slope > threshold → light smoothing
                    double tau = slope < slopeThresh ? tauFlat : tauEdge;
                    double a = 1.0 - Math.Exp(-(dt / tau));

                    yOut[i] = a * y[i] + (1 - a) * yOut[i - 1];
                }

                // Backward pass (zero-phase)
                for (int i = n - 2; i >= 0; i--)
                {
                    double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                    double slope = Math.Abs((y[i + 1] - y[i]) / dt);

                    double tau = slope < slopeThresh ? tauFlat : tauEdge;
                    double a = 1.0 - Math.Exp(-(dt / tau));

                    yOut[i] = a * yOut[i] + (1 - a) * yOut[i + 1];
                }

                // Re-assemble IntensityPoints
                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }

            public static List<IntensityPoint> SmoothOnlyFlat1(
                List<IntensityPoint> src,
                double tauFlat = 10.0,      // smoothing strength in flat regions
                double windowSec = 20.0,    // lookback/lookahead window for global slope
                double slopeThresh = 0.2    // max |ΔY/ΔX| considered "flat"
            )
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                // Extract arrays
                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                var yOut = new double[n];
                Array.Copy(y, yOut, n);

                // Forward pass
                for (int i = 1; i < n; i++)
                {
                    // Estimate global slope over ~windowSec backwards
                    int j = i;
                    while (j > 0 && (t[i] - t[j]) < windowSec) j--;
                    double dx = t[i] - t[j];
                    double dy = y[i] - y[j];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope < slopeThresh) // only smooth if globally flat
                    {
                        double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                        double a = 1.0 - Math.Exp(-(dt / tauFlat));
                        yOut[i] = a * y[i] + (1 - a) * yOut[i - 1];
                    }
                    else
                    {
                        yOut[i] = y[i]; // leave sharp changes untouched
                    }
                }

                // Backward pass (zero-phase)
                for (int i = n - 2; i >= 0; i--)
                {
                    int j = i;
                    while (j < n - 1 && (t[j] - t[i]) < windowSec) j++;
                    double dx = t[j] - t[i];
                    double dy = y[j] - y[i];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope < slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                        double a = 1.0 - Math.Exp(-(dt / tauFlat));
                        yOut[i] = a * yOut[i] + (1 - a) * yOut[i + 1];
                    }
                    else
                    {
                        yOut[i] = y[i];
                    }
                }

                // Re-assemble IntensityPoints
                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }

            public static List<IntensityPoint> SmoothOnlyFlat(
    List<IntensityPoint> src,
    double tauFlat = 10.0,      // smoothing strength in flat regions
    double windowSec = 20.0,    // lookback/lookahead window for global slope
    double slopeThresh = 0.2,   // max |ΔY/ΔX| considered "flat"
    double ignoreSec = 5.0      // ignore first N seconds
)
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                // Extract arrays
                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                var yOut = new double[n];
                Array.Copy(y, yOut, n);

                // Forward pass
                for (int i = 1; i < n; i++)
                {
                    if (t[i] <= ignoreSec) continue; // 🔑 keep raw data early on

                    // Estimate global slope over ~windowSec backwards
                    int j = i;
                    while (j > 0 && (t[i] - t[j]) < windowSec) j--;
                    double dx = t[i] - t[j];
                    double dy = y[i] - y[j];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope < slopeThresh) // only smooth if globally flat
                    {
                        double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                        double a = 1.0 - Math.Exp(-(dt / tauFlat));
                        yOut[i] = a * y[i] + (1 - a) * yOut[i - 1];
                    }
                }

                // Backward pass (zero-phase)
                for (int i = n - 2; i >= 0; i--)
                {
                    if (t[i] <= ignoreSec) continue; // 🔑 keep raw data early on

                    int j = i;
                    while (j < n - 1 && (t[j] - t[i]) < windowSec) j++;
                    double dx = t[j] - t[i];
                    double dy = y[j] - y[i];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope < slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                        double a = 1.0 - Math.Exp(-(dt / tauFlat));
                        yOut[i] = a * yOut[i] + (1 - a) * yOut[i + 1];
                    }
                }

                // Preserve endpoints
                yOut[0] = y[0];
                yOut[n - 1] = y[n - 1];

                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }


            public static List<IntensityPoint> SmoothOnlySlopes1(
    List<IntensityPoint> src,
    double tauEdge = 3.0,       // smoothing strength for steep ramps
    double windowSec = 10.0,    // lookback/lookahead window for slope estimate
    double slopeThresh = 0.5    // min |ΔY/ΔX| considered "steep"
)
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                // Extract arrays
                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                var yOut = new double[n];
                Array.Copy(y, yOut, n);

                // Forward pass
                for (int i = 1; i < n; i++)
                {
                    // Estimate slope over ~windowSec backwards
                    int j = i;
                    while (j > 0 && (t[i] - t[j]) < windowSec) j--;
                    double dx = t[i] - t[j];
                    double dy = y[i] - y[j];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope > slopeThresh) // only smooth if steep
                    {
                        double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                        double a = 1.0 - Math.Exp(-(dt / tauEdge));
                        yOut[i] = a * y[i] + (1 - a) * yOut[i - 1];
                    }
                    else
                    {
                        yOut[i] = y[i];
                    }
                }

                // Backward pass (zero-phase)
                for (int i = n - 2; i >= 0; i--)
                {
                    int j = i;
                    while (j < n - 1 && (t[j] - t[i]) < windowSec) j++;
                    double dx = t[j] - t[i];
                    double dy = y[j] - y[i];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope > slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                        double a = 1.0 - Math.Exp(-(dt / tauEdge));
                        yOut[i] = a * yOut[i] + (1 - a) * yOut[i + 1];
                    }
                    else
                    {
                        yOut[i] = y[i];
                    }
                }

                // Re-assemble IntensityPoints
                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }

            public static List<IntensityPoint> SmoothOnlySlopes2(
    List<IntensityPoint> src,
    double tauEdge = 3.0,
    double windowSec = 10.0,
    double slopeThresh = 0.5
)
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                var yOut = new double[n];
                Array.Copy(y, yOut, n);

                // Forward pass
                for (int i = 1; i < n; i++)
                {
                    int j = i;
                    while (j > 0 && (t[i] - t[j]) < windowSec) j--;
                    double dx = t[i] - t[j];
                    double dy = y[i] - y[j];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope > slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                        double a = 1.0 - Math.Exp(-(dt / tauEdge));
                        yOut[i] = a * y[i] + (1 - a) * yOut[i - 1];
                    }
                }

                // Backward pass
                for (int i = n - 2; i >= 0; i--)
                {
                    int j = i;
                    while (j < n - 1 && (t[j] - t[i]) < windowSec) j++;
                    double dx = t[j] - t[i];
                    double dy = y[j] - y[i];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope > slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                        double a = 1.0 - Math.Exp(-(dt / tauEdge));
                        yOut[i] = a * yOut[i] + (1 - a) * yOut[i + 1];
                    }
                }

                // 🔑 Preserve endpoints
                yOut[0] = y[0];
                yOut[n - 1] = y[n - 1];

                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }

            public static List<IntensityPoint> SmoothOnlySlopes(
    List<IntensityPoint> src,
    double tauEdge = 3.0,       // smoothing for steep ramps
    double windowSec = 10.0,    // slope-estimation window
    double slopeThresh = 0.5,   // slope cutoff
    double ignoreSec = 5.0      // ignore first N seconds
)
            {
                int n = src?.Count ?? 0;
                var res = new List<IntensityPoint>(n);
                if (n == 0) return res;

                var t = new double[n];
                var y = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = src[i].X;
                    y[i] = src[i].Y;
                    tx[i] = src[i].TargetX;
                    ty[i] = src[i].TargetY;
                }

                var yOut = new double[n];
                Array.Copy(y, yOut, n);

                // Forward pass
                for (int i = 1; i < n; i++)
                {
                    if (t[i] <= ignoreSec) continue; // 🔑 keep raw data early on

                    int j = i;
                    while (j > 0 && (t[i] - t[j]) < windowSec) j--;
                    double dx = t[i] - t[j];
                    double dy = y[i] - y[j];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope > slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i] - t[i - 1]);
                        double a = 1.0 - Math.Exp(-(dt / tauEdge));
                        yOut[i] = a * y[i] + (1 - a) * yOut[i - 1];
                    }
                }

                // Backward pass
                for (int i = n - 2; i >= 0; i--)
                {
                    if (t[i] <= ignoreSec) continue; // 🔑 keep raw data early on

                    int j = i;
                    while (j < n - 1 && (t[j] - t[i]) < windowSec) j++;
                    double dx = t[j] - t[i];
                    double dy = y[j] - y[i];
                    double slope = (dx > 1e-9) ? Math.Abs(dy / dx) : 0.0;

                    if (slope > slopeThresh)
                    {
                        double dt = Math.Max(1e-9, t[i + 1] - t[i]);
                        double a = 1.0 - Math.Exp(-(dt / tauEdge));
                        yOut[i] = a * yOut[i] + (1 - a) * yOut[i + 1];
                    }
                }

                // Preserve endpoints
                yOut[0] = y[0];
                yOut[n - 1] = y[n - 1];

                for (int i = 0; i < n; i++)
                    res.Add(new IntensityPoint(t[i], yOut[i], tx[i], ty[i]));

                return res;
            }


        }

        public static class IntensityNormalize
        {
            public sealed class Correction
            {
                public int Index;           // first sample AFTER the boundary
                public double Time;         // t[Index]
                public double Dx, Dy;       // px jump
                public double PreMedian;    // local median before
                public double PostMedian;   // local median after (raw)
                public double Gain;         // multiplicative factor applied to all samples >= Index
                public double Offset;       // additive offset applied after gain
                public override string ToString() =>
                    $"@{Time:F3}s Δpos=({Dx:F1},{Dy:F1}) gain={Gain:F4} offset={Offset:F3}";
            }

            /// <summary>
            /// Normalize intensity discontinuities caused by target jumps.
            /// For each detected jump, fit a simple affine correction on a short pre/post window and
            /// apply it to all subsequent samples so the series is continuous at the boundary.
            /// </summary>
            /// <param name="src">Time-ordered samples with TargetX/TargetY filled.</param>
            /// <param name="posJumpPx">Minimum Euclidean pixel jump to consider a boundary (e.g., 3 px).</param>
            /// <param name="winSec">Half-window (seconds) used on each side to estimate medians/slopes (e.g., 1.0 s).</param>
            /// <param name="minStep">Minimum intensity step (abs, same units as Y) to correct (e.g., 0.3).</param>
            /// <param name="preferGain">If true, try multiplicative first; else offset-first.</param>
            /// <param name="protectAroundSecs">Do not correct inside these [tStart, tEnd] intervals (e.g., around t0/t1/t2).</param>
            public static (List<IntensityPoint> Fixed, List<Correction> Applied) NormalizeByLockOnJumps(
                List<IntensityPoint> src,
                double posJumpPx = 3.0,
                double winSec = 1.0,
                double minStep = 0.3,
                bool preferGain = true,
                List<(double tStart, double tEnd)> protectAroundSecs = null)
            {
                var pts = (src ?? new List<IntensityPoint>());
                int n = pts.Count;
                var dst = new List<IntensityPoint>(n);
                if (n == 0) return (dst, new List<Correction>());

                // Copy first sample
                dst.Add(new IntensityPoint(pts[0].X, pts[0].Y, pts[0].TargetX, pts[0].TargetY));

                // Utilities
                Func<int, int, List<IntensityPoint>> slice = (a, b) =>
                {
                    var list = new List<IntensityPoint>(Math.Max(0, b - a + 1));
                    a = Math.Max(0, a);
                    b = Math.Min(n - 1, b);
                    for (int i = a; i <= b; i++) list.Add(pts[i]);
                    return list;
                };

                Func<IEnumerable<double>, double> median = xs =>
                {
                    var list = xs.ToList();
                    if (list.Count == 0) return double.NaN;
                    list.Sort();
                    int m = list.Count / 2;
                    return (list.Count % 2 == 1) ? list[m] : 0.5 * (list[m - 1] + list[m]);
                };

                // Build an index by time to do windowing in seconds
                double[] t = pts.Select(p => p.X).ToArray();

                // Keep current cumulative affine correction (gain/offset) applied to “future” samples
                double curGain = 1.0;
                double curOffset = 0.0;
                var applied = new List<Correction>();

                // Walk forward and adjust when we see a target jump
                for (int i = 1; i < n; i++)
                {
                    var prev = pts[i - 1];
                    var cur = pts[i];

                    // Apply current cumulative correction to build dst
                    double yCorr = cur.Y * curGain + curOffset;

                    // Check protection windows (don’t correct around true events)
                    bool protectedHere = protectAroundSecs != null &&
                                         protectAroundSecs.Any(iv => cur.X >= iv.tStart && cur.X <= iv.tEnd);

                    // Position jump?
                    bool havePos = !(double.IsNaN(prev.TargetX) || double.IsNaN(cur.TargetX));
                    double dx = havePos ? (cur.TargetX - prev.TargetX) : 0.0;
                    double dy = havePos ? (cur.TargetY - prev.TargetY) : 0.0;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    if (havePos && !protectedHere && dist >= posJumpPx)
                    {
                        // Build pre/post windows by time
                        // pre window ends at (i-1); post window starts at i
                        double tLeft = cur.X - winSec;
                        double tRight = cur.X + winSec;

                        // indices
                        int iPreA = Array.BinarySearch(t, tLeft); if (iPreA < 0) iPreA = ~iPreA;
                        int iPreB = i - 1;
                        int iPostA = i;
                        int iPostB = Array.BinarySearch(t, tRight); if (iPostB < 0) iPostB = ~iPostB - 1;

                        // Gather windows (use raw Y but we’ll map post→pre)
                        var preYs = slice(iPreA, iPreB).Select(p => p.Y).ToList();
                        var postYs = slice(iPostA, iPostB).Select(p => p.Y).ToList();

                        if (preYs.Count >= 5 && postYs.Count >= 5)
                        {
                            // Robust step estimate via medians
                            double preMed = median(preYs);
                            double postMed = median(postYs);
                            double step = postMed - preMed; // what changed at the boundary

                            if (Math.Abs(step) >= minStep)
                            {
                                // Decide correction model: offset, gain, or small affine
                                double gain = 1.0, offset = 0.0;

                                if (preferGain)
                                {
                                    // multiplicative first (avoid divide-by-zero)
                                    if (Math.Abs(postMed) > 1e-6)
                                    {
                                        gain = preMed / postMed;
                                        offset = 0.0;
                                    }
                                    else
                                    {
                                        gain = 1.0; offset = -step; // fallback to offset
                                    }
                                }
                                else
                                {
                                    // offset first
                                    gain = 1.0; offset = -step;
                                    // if pre/post around zero and noise is tiny, a small gain may be more stable:
                                    if (Math.Abs(postMed) > 1e-6 && Math.Abs(preMed) > 1e-6)
                                    {
                                        double g = preMed / postMed;
                                        if (g > 0.5 && g < 2.0 && Math.Abs(g - 1.0) > 0.05) { gain = g; offset = 0.0; }
                                    }
                                }

                                // Update cumulative correction for all future samples (including current)
                                curGain *= gain;
                                curOffset = curOffset * gain + offset; // compose affine: y' = (y*curGain) + curOffset

                                // Recompute corrected y for this sample with updated cumulative
                                yCorr = cur.Y * curGain + curOffset;

                                applied.Add(new Correction
                                {
                                    Index = i,
                                    Time = cur.X,
                                    Dx = dx,
                                    Dy = dy,
                                    PreMedian = preMed,
                                    PostMedian = postMed,
                                    Gain = gain,
                                    Offset = offset
                                });
                            }
                        }
                    }

                    dst.Add(new IntensityPoint(cur.X, yCorr, cur.TargetX, cur.TargetY));
                }

                return (dst, applied);
            }


            public static (List<IntensityPoint>, bool) CorrectForTargetDrift(
                List<IntensityPoint> pts,
                bool shiftForward = true,  // current behavior
                double jumpPxThreshold = 6.0,  // how big a motion counts as a drift
                int minJumpFrames = 2,    // must persist ≥ this many frames
                int settleWin = 8,    // consecutive frames within settlePx to call it “settled”
                double settlePx = 0.7,  // tight band for declaring settled
                int medianWin = 25    // frames to compute pre/post medians
            )
            {
                bool corrected = false;

                if (pts == null || pts.Count == 0) return (new List<IntensityPoint>(), false);
                // Work on a copy so we don't mutate callers' data
                var outPts = new List<IntensityPoint>(pts.Count);
                for (int j = 0; j < pts.Count; j++) outPts.Add(pts[j].Clone());

                // Simple helpers
                Func<int, int, double> median = (start, count) =>
                {
                    start = Math.Max(0, start);
                    count = Math.Max(0, Math.Min(count, outPts.Count - start));
                    var buf = new double[count];
                    for (int k = 0; k < count; k++) buf[k] = outPts[start + k].Y;
                    Array.Sort(buf);
                    if (count == 0) return double.NaN;
                    return (count % 2 == 1) ? buf[count / 2] : 0.5 * (buf[count / 2 - 1] + buf[count / 2]);
                };

                Func<int, int, double> maxDisp = (a, b) =>
                {
                    a = Math.Max(0, a); b = Math.Min(outPts.Count - 1, b);
                    double maxd = 0;
                    for (int k = a + 1; k <= b; k++)
                    {
                        double dx = Math.Abs(outPts[k].TargetX - outPts[k - 1].TargetX);
                        double dy = Math.Abs(outPts[k].TargetY - outPts[k - 1].TargetY);
                        maxd = Math.Max(maxd, Math.Max(dx, dy));
                    }
                    return maxd;
                };

                int i = 1;
                double cumulativeBias = 0.0;  // accumulate if multiple drifts happen
                while (i < outPts.Count)
                {
                    // Look for a jump that persists for minJumpFrames
                    int jumpStart = -1;
                    int jumpCount = 0;
                    int j = i;
                    while (j < outPts.Count)
                    {
                        double dx = Math.Abs(outPts[j].TargetX - outPts[j - 1].TargetX);
                        double dy = Math.Abs(outPts[j].TargetY - outPts[j - 1].TargetY);
                        bool jumped = (dx >= jumpPxThreshold) || (dy >= jumpPxThreshold);

                        if (jumped)
                        {
                            corrected = true;
                            if (jumpStart < 0) jumpStart = j - 1;
                            jumpCount++;
                            if (jumpCount >= minJumpFrames) break; // confirmed drift
                        }
                        else
                        {
                            // reset if a candidate fizzled
                            jumpStart = -1;
                            jumpCount = 0;
                        }
                        j++;
                    }

                    if (jumpStart < 0) break; // no more drifts

                    // Find settle end: first index after j where position remains within settlePx for 'settleWin' frames
                    int settleEnd = -1;
                    int k = Math.Min(j + 1, outPts.Count - 1);
                    while (k < outPts.Count - settleWin)
                    {
                        // band check over [k, k+settleWin]
                        double mxd = maxDisp(k, k + settleWin);
                        if (mxd <= settlePx)
                        {
                            // extend to the end of that quiet window
                            settleEnd = k + settleWin;
                            break;
                        }
                        k++;
                    }
                    if (settleEnd < 0) settleEnd = Math.Min(outPts.Count - 1, j + settleWin); // fallback

                    // 1) Pad the transient with last good intensity
                    int lastGood = Math.Max(0, jumpStart);
                    double hold = outPts[lastGood].Y; // already includes any previous bias
                    for (int t = jumpStart + 1; t <= settleEnd; t++)
                        outPts[t].Y = hold;

                    // 2) Compute post-event bias to realign medians
                    int preStart = Math.Max(0, jumpStart - medianWin + 1);
                    int preCount = jumpStart - preStart + 1;
                    int postStart = Math.Min(outPts.Count - 1, settleEnd + 1);
                    int postCount = Math.Max(0, Math.Min(medianWin, outPts.Count - postStart));

                    double preMed = median(preStart, preCount);
                    double postMed = median(postStart, postCount);

                    if (!double.IsNaN(preMed) && !double.IsNaN(postMed))
                    {
                        double bias = preMed - postMed;

                        // ✅ Guard goes HERE
                        double allowed = 0.25 * Math.Abs(preMed); // allow max 25% shift
                        if (Math.Abs(bias) < allowed)
                        {
                            cumulativeBias += bias;

                            if (shiftForward)
                            {
                                for (int t = postStart; t < outPts.Count; t++)
                                    outPts[t].Y += bias;
                            }
                            else
                            {
                                for (int t = 0; t <= jumpStart; t++)
                                    outPts[t].Y -= bias;
                            }
                        }
                        else
                        {
                            // optional logging for debugging
                            Console.WriteLine($"[DriftCorrector] Skipped large bias {bias:0.###} at frame {jumpStart}");
                        }

                    }

                    // Continue search after settleEnd
                    i = Math.Max(i + 1, settleEnd + 1);
                }

                return (outPts, corrected);
            }


        }

        public static class InflexionRelatedAnalysis
        {
            // Zero-phase windowed mean over [tStart, tEnd]; returns null if no points
            private static double? MeanBetween(IList<IntensityPoint> pts, double tStart, double tEnd)
            {
                if (pts == null || pts.Count == 0 || tEnd <= tStart) return null;
                double sum = 0.0; int count = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    double x = pts[i].X;
                    if (x < tStart) continue;
                    if (x > tEnd) break;
                    sum += pts[i].Y; count++;
                }
                return count > 0 ? (double?)(sum / count) : null;
            }

            // Symmetric local slope (units per second) using window h on either side; null if not enough support
            private static double? SymSlope(IList<IntensityPoint> pts, double t, double h)
            {
                double t0 = t - h, t1 = t + h;
                IntensityPoint p0 = default, p1 = default;
                bool have0 = false, have1 = false;

                for (int i = 0; i < pts.Count; i++)
                {
                    double x = pts[i].X;
                    if (!have0 && x >= t0) { p0 = pts[Math.Max(0, i - 1)]; have0 = true; }
                    if (!have1 && x >= t1) { p1 = pts[i]; have1 = true; break; }
                }
                if (!have0) p0 = pts[0];
                if (!have1) p1 = pts[pts.Count - 1];

                double dx = p1.X - p0.X;
                if (dx <= 1e-9) return null;
                return (p1.Y - p0.Y) / dx;
            }

            /// <summary>
            /// Find t0 by earliest two-window drop: first t where postMean - preMean <= -dropThresh,
            /// local symmetric slope <= -slopeThresh, and condition holds for holdSec.
            /// </summary>
            public static double? FindT0_TwoWindow(
                IList<IntensityPoint> pts,
                double wPreSec = 0.5,      // short pre window
                double wPostSec = 0.5,     // short post window
                double slopeHalfWin = 0.25,// slope support on each side
                double dropThresh = 0.25,  // required mean drop across the window (intensity units)
                double slopeThresh = 0.40, // required instantaneous fall rate (units/sec)
                double holdSec = 0.2,      // require it to persist briefly
                double epsilon = 0.02      // noise slack
            )
            {
                if (pts == null || pts.Count < 5) return null;

                // start near the beginning but leave tiny margin so windows fit
                double startT = pts[0].X + Math.Max(wPreSec, slopeHalfWin) + 1e-6;
                double endT = pts[pts.Count - 1].X - Math.Max(wPostSec, 0.0) - 1e-6;

                for (int i = 0; i < pts.Count; i++)
                {
                    double t = pts[i].X;
                    if (t < startT) continue;
                    if (t > endT) break;

                    var pre = MeanBetween(pts, t - wPreSec, t);
                    var post = MeanBetween(pts, t, t + wPostSec);
                    if (!pre.HasValue || !post.HasValue) continue;

                    double delta = post.Value - pre.Value;          // negative when dropping
                    var slope = SymSlope(pts, t, slopeHalfWin);     // negative when dropping
                    if (!slope.HasValue) continue;

                    if (delta <= -(dropThresh - epsilon) && slope.Value <= -(slopeThresh - 1e-6))
                    {
                        // quick hold: ensure condition persists for holdSec
                        double tHoldEnd = t + holdSec;
                        bool ok = true;
                        for (int j = i; j < pts.Count && pts[j].X <= tHoldEnd; j++)
                        {
                            var preH = MeanBetween(pts, pts[j].X - wPreSec, pts[j].X);
                            var postH = MeanBetween(pts, pts[j].X, pts[j].X + wPostSec);
                            var slopeH = SymSlope(pts, pts[j].X, slopeHalfWin);
                            if (!preH.HasValue || !postH.HasValue || !slopeH.HasValue) { ok = false; break; }
                            double dH = postH.Value - preH.Value;
                            if (!(dH <= -(dropThresh - epsilon) && slopeH.Value <= -(slopeThresh - 1e-6)))
                            {
                                ok = false; break;
                            }
                        }
                        if (ok) return t; // earliest qualifying change
                    }
                }
                return null;
            }

            public static double? FindT0ShutterOpen(
                IList<IntensityPoint> points,
                double lookbackSec = 0.20,   // mean “before” window (0.5)
                double lookaheadSec = 0.20,   // mean “after” window (0.3)
                double minDropAbs = 0.40,   // absolute drop in intensity units
                double minSigma = 3.0    // how many sigmas drop must exceed
            )
            {
                if (points == null || points.Count < 5) return null;

                // Extract arrays (assumes points are sorted by X time; sort if unsure)
                int n = points.Count;
                var t = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++) { t[i] = points[i].X; y[i] = points[i].Y; }

                // Prefix sums for O(1) window means / stddev
                var ps = new double[n + 1]; // sum y
                var ps2 = new double[n + 1]; // sum y^2
                for (int i = 0; i < n; i++) { ps[i + 1] = ps[i] + y[i]; ps2[i + 1] = ps2[i] + y[i] * y[i]; }

                // Two moving pointers to find window bounds by time
                int lb = 0; // left bound for lookback (exclusive of i)
                int rb = 0; // right bound for lookahead (inclusive of rb-1)

                double bestScore = double.NegativeInfinity;
                int bestIndex = -1;

                for (int i = 0; i < n; i++)
                {
                    // Move lb up while the window is too large (keep times in (t[i]-lookback, t[i]))
                    while (lb < i && t[i] - t[lb] > lookbackSec) lb++;

                    // Advance rb so that t[rb] - t[i] <= lookaheadSec
                    if (rb < i + 1) rb = i + 1;
                    while (rb < n && t[rb] - t[i] <= lookaheadSec) rb++;

                    // Pre-window: [lb, i)    Post-window: [i, rb)
                    int preCount = Math.Max(0, i - lb);
                    int postCount = Math.Max(0, rb - i);

                    if (preCount < 2 || postCount < 1) continue; // need some history and some future

                    double preSum = ps[i] - ps[lb];
                    double preSum2 = ps2[i] - ps2[lb];
                    double postSum = ps[rb] - ps[i];

                    double preMean = preSum / preCount;
                    double postMean = postSum / postCount;

                    // Pre-window stddev (population-ish; add epsilon)
                    double preVar = Math.Max(0.0, preSum2 / preCount - preMean * preMean);
                    double preStd = Math.Sqrt(preVar) + 1e-9;

                    double drop = preMean - postMean;     // positive when it drops
                    double z = drop / preStd;          // sigma-scaled drop
                    double score = (drop >= minDropAbs && z >= minSigma) ? z : double.NegativeInfinity;

                    // We want the first strong, sustained drop — prefer earlier i if scores tie
                    if (score > bestScore || (score == bestScore && bestIndex >= 0 && t[i] < t[bestIndex]))
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                // Return candidate time (center) if we found a valid event
                return (bestIndex >= 0 && !double.IsInfinity(bestScore)) ? t[bestIndex] : (double?)null;
            }

            public static double? FindT1FirstBasinInWindow(
                IList<IntensityPoint> pts,
                double t0,
                double winStartAfterT0 = 6.5,   // e.g., 6.7 → t0+6.7 ≈ 11.3 for your file
                double winEndAfterT0 = 8.0,   // e.g., 7.5 → t0+7.5 ≈ 12.1
                double emaAlpha = 0.25,
                double postWinSec = 1.5,
                double slopeTol = 0.02,         // |dy/dt| ≤ tol → “flat”
                double extraDropTol = 0.05     // allow ≤ 0.05 further drop in post window
            )
            {
                if (pts == null || pts.Count < 3) return null;

                int n = pts.Count;
                var t = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++) { t[i] = pts[i].X; y[i] = pts[i].Y; }

                // EMA smooth
                var ys = new double[n];
                double acc = y[0]; ys[0] = acc;
                for (int i = 1; i < n; i++) { acc = emaAlpha * y[i] + (1 - emaAlpha) * acc; ys[i] = acc; }

                // derivative
                var dydt = new double[n];
                dydt[0] = (ys[1] - ys[0]) / Math.Max(1e-9, t[1] - t[0]);
                for (int i = 1; i < n - 1; i++)
                {
                    double dt = Math.Max(1e-9, t[i + 1] - t[i - 1]);
                    dydt[i] = (ys[i + 1] - ys[i - 1]) / dt;
                }
                dydt[n - 1] = (ys[n - 1] - ys[n - 2]) / Math.Max(1e-9, t[n - 1] - t[n - 2]);

                // time window
                double tStart = t0 + winStartAfterT0;
                double tEnd = t0 + winEndAfterT0;
                int iStart = Array.FindIndex(t, x => x >= tStart); if (iStart < 0) return null;
                int iEnd = Array.FindIndex(t, x => x > tEnd); if (iEnd < 0) iEnd = n;
                if (iEnd - iStart < 2) return null;

                // earliest qualifying local minimum
                for (int i = Math.Max(iStart + 1, 1); i <= Math.Min(iEnd - 2, n - 2); i++)
                {
                    if (!(ys[i] <= ys[i - 1] && ys[i] <= ys[i + 1])) continue;

                    // post window [i .. rb]
                    int rb = i;
                    while (rb < iEnd - 1 && (t[rb + 1] - t[i]) <= postWinSec) rb++;

                    // mean post slope
                    double postSlope = 0.0;
                    for (int k = i; k <= rb; k++) postSlope += dydt[k];
                    postSlope /= Math.Max(1, rb - i + 1);

                    // ensure it doesn't keep falling meaningfully after i
                    double minAfter = ys[i];
                    for (int k = i; k <= rb; k++) if (ys[k] < minAfter) minAfter = ys[k];
                    double furtherDrop = ys[i] - minAfter;

                    if (postSlope >= -slopeTol && furtherDrop <= extraDropTol)
                        return t[i]; // earliest basin in the window
                }

                // fallback: absolute minimum within [tStart,tEnd]
                int iMin = iStart;
                for (int i = iStart + 1; i < iEnd; i++) if (ys[i] < ys[iMin]) iMin = i;
                return t[iMin];
            }

            public static double? FindT1_FirstPlateauAfterDrop(
                IList<IntensityPoint> points,
                double t0,
                double emaAlpha = 0.25,  // smoothing (0..1)
                double preDescentSec = 0.8,   // must be descending continuously for at least this long
                double slopeNegTol = 0.08,  // % per second; below this = “descending”
                double flatSec = 0.8,   // must be flat for at least this long
                double slopeFlatTol = 0.02,  // % per second; within ±this = “flat”
                double confirmSec = 1.0,   // look ahead this long to ensure no meaningful further drop
                double extraDropTol = 0.10   // %; allow this much residual drop while still calling it a plateau
            )
            {
                if (points == null || points.Count < 5) return null;

                // 1) arrays (assume sorted by time)
                int n = points.Count;
                var t = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++) { t[i] = points[i].X; y[i] = points[i].Y; }

                // 2) EMA smooth
                var ys = new double[n];
                double acc = y[0]; ys[0] = acc;
                for (int i = 1; i < n; i++) { acc = emaAlpha * y[i] + (1 - emaAlpha) * acc; ys[i] = acc; }

                // 3) central-diff derivative (dy/dt)
                var dydt = new double[n];
                dydt[0] = (ys[1] - ys[0]) / Math.Max(1e-9, t[1] - t[0]);
                for (int i = 1; i < n - 1; i++)
                {
                    double dt2 = Math.Max(1e-9, t[i + 1] - t[i - 1]);
                    dydt[i] = (ys[i + 1] - ys[i - 1]) / dt2;
                }
                dydt[n - 1] = (ys[n - 1] - ys[n - 2]) / Math.Max(1e-9, t[n - 1] - t[n - 2]);

                // 4) start after t0
                int i0 = 0; while (i0 < n && t[i0] < t0) i0++;
                if (i0 >= n - 2) return null;

                // helpers to advance by time
                Func<int, double, int> advanceToTime = (startIdx, horizonSec) =>
                {
                    int j = startIdx;
                    double tStart = t[startIdx];
                    while (j + 1 < n && t[j + 1] - tStart <= horizonSec) j++;
                    return j;
                };

                // 5) scan: require continuous descent, then continuous flat; confirm no further meaningful drop
                double descentStartTime = double.NaN;
                int plateauStart = -1;

                for (int i = i0 + 1; i < n - 1; i++)
                {
                    double dtPrev = Math.Max(1e-9, t[i] - t[i - 1]);

                    // Track continuous descent segment
                    bool descending = (dydt[i] < -slopeNegTol);
                    if (descending)
                    {
                        if (double.IsNaN(descentStartTime)) descentStartTime = t[i - 1];
                    }
                    else
                    {
                        // broke descent
                        descentStartTime = double.NaN;
                    }

                    // If we have satisfied pre-descent duration, start looking for a flat run
                    bool preDescentOk = !double.IsNaN(descentStartTime) && (t[i] - descentStartTime >= preDescentSec);

                    // Track a candidate flat run (continuous |slope| ≤ tol)
                    bool isFlatHere = Math.Abs(dydt[i]) <= slopeFlatTol;
                    if (preDescentOk && isFlatHere)
                    {
                        if (plateauStart < 0) plateauStart = i; // begin flat segment
                        double flatDur = t[i] - t[plateauStart];

                        if (flatDur >= flatSec)
                        {
                            // Confirm: in the next confirmSec, it doesn't drop more than extraDropTol
                            int rb = advanceToTime(i, confirmSec);
                            double yAtStart = ys[i];
                            double minAhead = yAtStart;
                            for (int k = i; k <= rb; k++) if (ys[k] < minAhead) minAhead = ys[k];
                            double furtherDrop = yAtStart - minAhead;

                            if (furtherDrop <= extraDropTol)
                            {
                                // Earliest qualifying plateau: return midpoint of flat run
                                double tMid = 0.5 * (t[plateauStart] + t[i]);
                                return tMid;
                            }
                        }
                    }
                    else
                    {
                        // left flat band -> reset plateau tracker
                        plateauStart = -1;
                    }
                }

                // Fallback: if we never confirmed a plateau, pick earliest local minimum after t0
                for (int i = Math.Max(i0 + 1, 1); i < n - 1; i++)
                {
                    if (ys[i] <= ys[i - 1] && ys[i] <= ys[i + 1])
                        return t[i];
                }

                return null;
            }


            // Linear interpolation of Y at time t (assumes pts sorted by X)
            public static double? InterpolateYAt(List<IntensityPoint> pts, double t)
            {
                if (pts == null || pts.Count == 0) return null;
                int n = pts.Count;
                if (t <= pts[0].X) return pts[0].Y;
                if (t >= pts[n - 1].X) return pts[n - 1].Y;

                // binary search for right-hand index
                int lo = 0, hi = n - 1;
                while (lo + 1 < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (pts[mid].X <= t) lo = mid; else hi = mid;
                }

                var p0 = pts[lo];
                var p1 = pts[hi];
                double dt = p1.X - p0.X;
                if (dt <= 0) return p0.Y;
                double a = (t - p0.X) / dt;
                return p0.Y + a * (p1.Y - p0.Y);
            }

            // t3: first time >= t2 where intensity crosses back up to y(t1) and
            // remains >= that level for holdSec seconds (with small epsilon tolerance).
            public static double? FindT3_ReturnsToT1Level(
                List<IntensityPoint> pts,
                double t1, double t2,
                double holdSec = 3.0,
                double epsilon = 0.05)   // small slack to avoid noise flutters
            {
                if (pts == null || pts.Count < 2) return null;

                var yT1 = InterpolateYAt(pts, t1);
                if (!yT1.HasValue) return null;
                double level = yT1.Value;

                // start index at first sample >= t2 (but not before t1)
                double startTime = Math.Max(t1, t2);
                int i = 0;
                while (i < pts.Count && pts[i].X < startTime) i++;
                if (i >= pts.Count) return null;

                for (; i < pts.Count; i++)
                {
                    // Check if this sample is at/above level (with epsilon)
                    if (pts[i].Y + epsilon >= level)
                    {
                        // Refine the crossing time by linear interpolation from previous point if needed
                        double tCross = pts[i].X;
                        if (i > 0 && pts[i - 1].Y + epsilon < level)
                        {
                            double x0 = pts[i - 1].X, y0 = pts[i - 1].Y;
                            double x1 = pts[i].X, y1 = pts[i].Y;
                            double dx = x1 - x0, dy = y1 - y0;
                            if (dx > 0 && Math.Abs(dy) > 1e-12)
                            {
                                double frac = (level - y0) / dy;
                                if (frac >= 0 && frac <= 1) tCross = x0 + frac * dx;
                            }
                        }

                        // Verify it stays ≥ level for holdSec seconds (discrete check)
                        double holdEnd = tCross + holdSec;
                        int j = i;
                        bool ok = true;
                        // must have samples covering the whole interval up to holdEnd
                        while (j < pts.Count && pts[j].X < holdEnd)
                        {
                            if (pts[j].Y + epsilon < level) { ok = false; break; }
                            j++;
                        }
                        if (!ok) continue;

                        // Ensure the last available sample crosses/extends past holdEnd
                        if (j >= pts.Count)
                            return null; // not enough data to assert the hold

                        // Also check the sample at/after holdEnd
                        if (pts[j].Y + epsilon < level) continue;

                        return tCross; // earliest qualifying return that holds
                    }
                }

                return null;
            }

            public static double? FindT3_OnsetBySlopeChange(
                IList<IntensityPoint> points,
                double searchStart,                // start after t2 (or t1/t0 if t2 unknown)
                double emaAlpha = 0.25,            // smoothing for y
                double slopeWinSec = 1.0,          // width of regression window (short = earlier)
                double baselineLookbackSec = 12.0, // learn slope baseline before searchStart
                double zThreshold = 2.0,           // how many sigmas above baseline slope
                double minSlope = 0.06,            // absolute slope floor (%/s)
                double holdSec = 1.0,              // must persist for this long
                double riseAbs = 0.15              // also be ≥ baseline level + Δ (small)
            )
            {
                if (points == null || points.Count < 5) return null;

                // Extract arrays (assumed sorted by time)
                int n = points.Count;
                var t = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++) { t[i] = points[i].X; y[i] = points[i].Y; }

                // EMA smooth
                var ys = new double[n];
                double acc = y[0]; ys[0] = acc;
                for (int i = 1; i < n; i++) { acc = emaAlpha * y[i] + (1 - emaAlpha) * acc; ys[i] = acc; }

                // Rolling regression slope over slopeWinSec (robust to irregular sampling)
                double[] slope = new double[n];
                int a = 0;
                for (int i = 0; i < n; i++)
                {
                    while (a < i && t[i] - t[a] > slopeWinSec) a++;
                    int b = i + 1; // [a, b)
                    int m = b - a;
                    if (m < 3) { slope[i] = 0; continue; }

                    double sx = 0, sy = 0, sxx = 0, sxy = 0;
                    for (int k = a; k < b; k++) { double xi = t[k], yi = ys[k]; sx += xi; sy += yi; sxx += xi * xi; sxy += xi * yi; }
                    double denom = m * sxx - sx * sx;
                    slope[i] = (Math.Abs(denom) < 1e-9) ? 0.0 : (m * sxy - sx * sy) / denom; // dy/dt
                }

                // Baseline slope stats from [searchStart - baselineLookbackSec, searchStart]
                int iStart = 0; while (iStart < n && t[iStart] < searchStart) iStart++;
                double baseFromT = Math.Max(t[0], searchStart - baselineLookbackSec);

                double mu = 0, s2 = 0; int c = 0;
                for (int i = 0; i < iStart; i++)
                {
                    if (t[i] >= baseFromT)
                    {
                        double v = slope[i];
                        mu += v; s2 += v * v; c++;
                    }
                }
                double meanSlopeBase = (c > 0) ? (mu / c) : 0.0;
                double stdSlopeBase = (c > 0) ? Math.Sqrt(Math.Max(0.0, s2 / c - meanSlopeBase * meanSlopeBase)) : 0.0;

                // Rolling baseline level (for a small absolute rise check)
                Func<int, double> baselineLevel = i =>
                {
                    double from = Math.Max(t[0], t[i] - baselineLookbackSec);
                    double sum = 0; int cnt = 0;
                    for (int k = i; k >= 0 && t[k] >= from; k--) { sum += ys[k]; cnt++; }
                    return cnt > 0 ? sum / cnt : ys[i];
                };

                // Scan forward: earliest sustained slope jump
                int holdStart = -1;
                for (int i = iStart; i < n; i++)
                {
                    bool slopeHigh = slope[i] >= minSlope;

                    // z-score vs baseline; if baseline is ultra-stable (std~0), just use slopeHigh
                    bool zHigh = (stdSlopeBase > 1e-6) ? ((slope[i] - meanSlopeBase) >= zThreshold * stdSlopeBase) : slopeHigh;

                    bool levelHigh = ys[i] >= baselineLevel(i) + riseAbs;

                    if (slopeHigh && zHigh && levelHigh)
                    {
                        if (holdStart < 0) holdStart = i;
                        if (t[i] - t[holdStart] >= holdSec)
                            return t[holdStart];
                    }
                    else
                    {
                        holdStart = -1;
                    }
                }

                return null;
            }

            public static double? FindT3_OnsetBySlopeJump(
                IList<IntensityPoint> points,
                double searchStart,               // start after t2 (else t1/t0)
                double emaAlpha = 0.25,           // smoothing for y
                double preWinSec = 1.2,           // slope window BEFORE i
                double postWinSec = 0.8,          // slope window AFTER  i
                double slopeJump = 0.07,          // required jump: postSlope - preSlope (in %/s)
                double consecHoldSec = 0.6,       // how long the condition must hold
                double minRiseNextSec = 0.10,     // min absolute rise within the next 1s (in %)
                double riseWindowSec = 1.0        // forward look for rise
            )
            {
                if (points == null || points.Count < 5) return null;

                int n = points.Count;
                var t = new double[n];
                var y = new double[n];
                for (int i = 0; i < n; i++) { t[i] = points[i].X; y[i] = points[i].Y; }

                // 1) EMA smoothing
                var ys = new double[n];
                double acc = y[0]; ys[0] = acc;
                for (int i = 1; i < n; i++) { acc = emaAlpha * y[i] + (1 - emaAlpha) * acc; ys[i] = acc; }

                // helpers
                Func<int, int, double> mean = (a, b) => {
                    double s = 0; int c = 0; for (int k = a; k < b; k++) { s += ys[k]; c++; }
                    return c > 0 ? s / c : double.NaN;
                };

                Func<int, int, double> slopeLS = (a, b) => {
                    int m = b - a; if (m < 3) return 0.0;
                    double sx = 0, sy_ = 0, sxx = 0, sxy = 0;
                    for (int k = a; k < b; k++) { double xi = t[k], yi = ys[k]; sx += xi; sy_ += yi; sxx += xi * xi; sxy += xi * yi; }
                    double denom = m * sxx - sx * sx; if (Math.Abs(denom) < 1e-9) return 0.0;
                    return (m * sxy - sx * sy_) / denom; // dy/dt
                };

                Func<int, double, int> idxAfter = (start, dt) => {
                    int j = start;
                    double target = t[start] + dt;
                    while (j + 1 < n && t[j + 1] <= target) j++;
                    return j;
                };

                // start index
                int iStart = 0; while (iStart < n && t[iStart] < searchStart) iStart++;
                if (iStart >= n - 2) return null;

                int holdStart = -1;

                for (int i = iStart; i < n; i++)
                {
                    // pre window [iPreA, i)
                    int iPreA = i;
                    while (iPreA > 0 && t[i] - t[iPreA - 1] <= preWinSec) iPreA--;
                    if (i - iPreA < 3) { holdStart = -1; continue; }

                    // post window (i, iPostB]
                    int iPostB = idxAfter(i, postWinSec);
                    int iPostA = i + 1;
                    if (iPostB - iPostA < 2) { holdStart = -1; continue; }

                    double preSlope = slopeLS(iPreA, i);          // up to but not including i
                    double postSlope = slopeLS(iPostA, iPostB + 1);  // strictly after i

                    // forward rise within ~1s
                    int iRiseB = idxAfter(i, riseWindowSec);
                    double yNow = ys[i];
                    double yMaxFwd = yNow;
                    for (int k = i; k <= iRiseB; k++) if (ys[k] > yMaxFwd) yMaxFwd = ys[k];
                    double rise = yMaxFwd - yNow;

                    bool jumpOk = (postSlope - preSlope) >= slopeJump;
                    bool riseOk = rise >= minRiseNextSec;

                    if (jumpOk && riseOk)
                    {
                        if (holdStart < 0) holdStart = i;
                        // met long enough?
                        if (t[i] - t[holdStart] >= consecHoldSec)
                            return t[holdStart];
                    }
                    else
                    {
                        holdStart = -1;
                    }
                }

                return null;
            }
        }

        public struct SlopePoint
        {
            public double T;   // time (s)
            public double S;   // slope dY/dt (intensity per second)
            public SlopePoint(double t, double s) { T = t; S = s; }
        }

        public static class RollingSlopeAnalysis
        {
            // ----------------------------
            // Rolling (trailing) slope
            // ----------------------------

            // Regression slope over the last N samples (trailing window). Returns one SlopePoint per input row.
            // For rows that don't have N samples yet, returns NaN.
            public static List<SlopePoint> ComputeRollingSlopeBySamples(List<IntensityPoint> pts, int windowSamples)
            {
                var n = pts?.Count ?? 0;
                var outS = new List<SlopePoint>(n);
                if (n == 0 || windowSamples < 2) return outS;

                double sx = 0, sy = 0, sxx = 0, sxy = 0;
                int a = 0; // window start index (inclusive)

                for (int i = 0; i < n; i++)
                {
                    double xi = pts[i].X, yi = pts[i].Y;
                    sx += xi; sy += yi; sxx += xi * xi; sxy += xi * yi;

                    int m = i - a + 1;
                    if (m > windowSamples)
                    {
                        // pop oldest
                        double xo = pts[a].X, yo = pts[a].Y;
                        sx -= xo; sy -= yo; sxx -= xo * xo; sxy -= xo * yo;
                        a++;
                        m--;
                    }

                    double slope = 0.0; // previously double.NaN;
                    if (m >= windowSamples)
                    {
                        double denom = m * sxx - sx * sx;
                        slope = Math.Abs(denom) < 1e-12 ? 0.0 : (m * sxy - sx * sy) / denom; // previously double.NaN not 0.0
                    }

                    outS.Add(new SlopePoint(pts[i].X, slope));
                }
                return outS;
            }

            // Regression slope over a trailing TIME window (seconds). Adaptive to irregular sampling.
            // For rows that don't have enough time coverage yet, returns NaN.
            public static List<SlopePoint> ComputeRollingSlopeByTime(List<IntensityPoint> pts, double windowSec)
            {
                var n = pts?.Count ?? 0;
                var outS = new List<SlopePoint>(n);
                if (n == 0 || windowSec <= 0) return outS;

                int a = 0; // left bound (inclusive)
                for (int i = 0; i < n; i++)
                {
                    double tNow = pts[i].X;
                    while (a < i && pts[a].X < tNow - windowSec) a++;

                    // accumulate on the fly
                    double sx = 0, sy = 0, sxx = 0, sxy = 0;
                    int m = 0;
                    for (int k = a; k <= i; k++)
                    {
                        double xk = pts[k].X, yk = pts[k].Y;
                        sx += xk; sy += yk; sxx += xk * xk; sxy += xk * yk;
                        m++;
                    }

                    double slope = double.NaN;
                    if (m >= 3)
                    {
                        double denom = m * sxx - sx * sx;
                        slope = Math.Abs(denom) < 1e-12 ? double.NaN : (m * sxy - sx * sy) / denom;
                    }
                    outS.Add(new SlopePoint(tNow, slope));
                }
                return outS;
            }

            // Optional: simple trailing moving average on the slope (noise tamer).
            public static List<SlopePoint> SmoothSlopeMA(List<SlopePoint> s, int span)
            {
                var n = s?.Count ?? 0;
                var outS = new List<SlopePoint>(n);
                if (n == 0 || span <= 1) return new List<SlopePoint>(s ?? new List<SlopePoint>());

                double acc = 0; int a = 0;
                for (int i = 0; i < n; i++)
                {
                    double v = s[i].S;
                    acc += double.IsNaN(v) ? 0 : v;
                    if (i - a + 1 > span)
                    {
                        double vo = s[a].S;
                        acc -= double.IsNaN(vo) ? 0 : vo;
                        a++;
                    }
                    int m = i - a + 1;
                    double avg = (m >= span) ? (acc / span) : 0.0; // previously double.NaN
                    outS.Add(new SlopePoint(s[i].T, avg));
                }
                return outS;
            }

            // ----------------------------
            // Helpers
            // ----------------------------

            // Binary search: first index i where pts[i].X >= t (or pts.Count if none).
            private static int IndexAtOrAfter(List<IntensityPoint> pts, double t)
            {
                int lo = 0, hi = pts.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (pts[mid].X < t) lo = mid + 1; else hi = mid;
                }
                return lo;
            }

            // Binary search on slope array (by T)
            private static int IndexAtOrAfter(List<SlopePoint> s, double t)
            {
                int lo = 0, hi = s.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (s[mid].T < t) lo = mid + 1; else hi = mid;
                }
                return lo;
            }

            // Interpolate Y at time t (for t3 level check)
            public static double? InterpolateYAt(List<IntensityPoint> pts, double t)
            {
                if (pts == null || pts.Count == 0) return null;
                int n = pts.Count;
                if (t <= pts[0].X) return pts[0].Y;
                if (t >= pts[n - 1].X) return pts[n - 1].Y;

                int lo = 0, hi = n - 1;
                while (lo + 1 < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (pts[mid].X <= t) lo = mid; else hi = mid;
                }
                var p0 = pts[lo]; var p1 = pts[hi];
                double dt = p1.X - p0.X; if (dt <= 0) return p0.Y;
                double a = (t - p0.X) / dt;
                return p0.Y + a * (p1.Y - p0.Y);
            }

            // ----------------------------
            // Detectors (slope-driven)
            // ----------------------------

            // t0: earliest time the rolling slope is <= negThreshold and stays ≤ for holdSec
            public static double? FindT0_FromSlope(List<IntensityPoint> pts, List<SlopePoint> slope,
                double negThreshold = -0.15, double holdSec = 3.0)
            {
                if (pts == null || pts.Count == 0 || slope == null || slope.Count == 0) return null;
                int n = slope.Count;

                int holdStart = -1;
                for (int i = 0; i < n; i++)
                {
                    double s = slope[i].S;
                    if (!double.IsNaN(s) && s <= negThreshold)
                    {
                        if (holdStart < 0) holdStart = i;
                        // check time span
                        if (slope[i].T - slope[holdStart].T >= holdSec)
                            return slope[holdStart].T;
                    }
                    else
                    {
                        holdStart = -1;
                    }
                }
                return null;
            }

            // t1: first time AFTER t0 when |slope| <= nearZero for holdSec (i.e., "flat-ish")
            public static double? FindT1_FromSlopeNearZero(List<IntensityPoint> pts, List<SlopePoint> slope,
                double t0, double nearZero = 0.2, double holdSec = 3.0)
            {
                if (pts == null || pts.Count == 0 || slope == null || slope.Count == 0) return null;
                int n = slope.Count;
                int iStart = IndexAtOrAfter(slope, t0);

                int holdStart = -1;
                for (int i = iStart; i < n; i++)
                {
                    double s = slope[i].S;
                    if (!double.IsNaN(s) && Math.Abs(s) <= nearZero)
                    {
                        if (holdStart < 0) holdStart = i;
                        if (slope[i].T - slope[holdStart].T >= holdSec)
                            return slope[holdStart].T;
                    }
                    else
                    {
                        holdStart = -1;
                    }
                }
                return null;
            }

            // t3: earliest time ≥ t2 where:
            //   (A) smoothed slope ≥ posThreshold for slopeHoldSec (sustained rise), AND
            //   (B) intensity has been ≥ y(t1) - epsilon for levelHoldSec (return to t1 level).
            // Returns the EARLIER of:
            //   - slope-criterion start time, and
            //   - first crossing that satisfies the level hold,
            // as soon as both are satisfied (you can swap the policy if you prefer).
            public static double? FindT3_FromSlopeAndLevel(List<IntensityPoint> pts, List<SlopePoint> slope,
                double t1, double t2,
                double posThreshold = 0.02, double slopeHoldSec = 7.5,
                double levelHoldSec = 30.0, double epsilon = 0.05)
            {
                if (pts == null || pts.Count == 0 || slope == null || slope.Count == 0) return null;

                double level = InterpolateYAt(pts, t1) ?? double.NaN;
                if (double.IsNaN(level)) return null;

                int iS = IndexAtOrAfter(slope, t2);
                int iY = IndexAtOrAfter(pts, t2);

                // Track holds independently
                int sHoldStart = -1; double? tSlopeOK = null;
                int yHoldStart = -1; double? tLevelOK = null;

                // We'll walk forward by time, stepping through whichever series is earlier
                //int i = 0; // merged cursor (we’ll just advance both in their own loops)
                double tMax = Math.Max(slope[slope.Count - 1].T, pts[pts.Count - 1].X);
                double tCursor = Math.Max(t2, Math.Min(slope[iS].T, pts[iY].X));

                while (tCursor <= tMax)
                {
                    // Advance slope index to current time
                    while (iS + 1 < slope.Count && slope[iS + 1].T <= tCursor) iS++;
                    // Advance intensity index likewise
                    while (iY + 1 < pts.Count && pts[iY + 1].X <= tCursor) iY++;

                    // --- Slope hold ---
                    double sVal = slope[iS].S;
                    if (!double.IsNaN(sVal) && sVal >= posThreshold)
                    {
                        if (sHoldStart < 0) sHoldStart = iS;
                        if (slope[iS].T - slope[sHoldStart].T >= slopeHoldSec)
                            tSlopeOK = slope[sHoldStart].T;
                    }
                    else sHoldStart = -1;

                    // --- Level hold (>= y(t1)-eps) ---
                    double yVal = pts[iY].Y;
                    if (yVal + epsilon >= level)
                    {
                        if (yHoldStart < 0) yHoldStart = iY;
                        if (pts[iY].X - pts[yHoldStart].X >= levelHoldSec)
                            tLevelOK = pts[yHoldStart].X;
                    }
                    else yHoldStart = -1;

                    // If both conditions satisfied, take the earlier of the two start times
                    if (tSlopeOK.HasValue && tLevelOK.HasValue)
                        return Math.Max(tSlopeOK.Value, tLevelOK.Value);

                    // advance time cursor by the smaller next step
                    double nextS = (iS + 1 < slope.Count) ? slope[iS + 1].T : double.PositiveInfinity;
                    double nextY = (iY + 1 < pts.Count) ? pts[iY + 1].X : double.PositiveInfinity;
                    double nextT = Math.Min(nextS, nextY);
                    if (!double.IsInfinity(nextT)) tCursor = nextT; else break;
                }

                if (tSlopeOK.HasValue)
                    return tSlopeOK.Value;
                else if (tLevelOK.HasValue)
                    return tLevelOK.Value;

                return null;
            }
        }

        public static class DataExporter
        {
            public static string MakeSafe(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "video";
                foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
                return s;
            }

            public static double[] BuildHeatmap(double[] times, double t, double sigma)
            {
                var y = new double[times.Length];
                if (double.IsNaN(t) || double.IsInfinity(t)) return y;
                double twoSigma2 = 2 * sigma * sigma;
                for (int i = 0; i < times.Length; i++)
                {
                    double d = times[i] - t;
                    y[i] = Math.Exp(-(d * d) / twoSigma2);
                }
                return y;
            }

            // Regions: 1: t < t0
            //          2: t0 ≤ t < t1
            //          3: t1 ≤ t < t2
            //          4: t2 ≤ t < t3
            //          5: t ≥ t3 until plateau end
            //          6: (optional long tail plateau; we map >=t3 to 5; keep 6 if you later detect t4)
            public static int[] BuildRegions(double[] times, double t0, double t1, double t2, double t3)
            {
                var reg = new int[times.Length];
                for (int i = 0; i < times.Length; i++)
                {
                    double t = times[i];
                    int r;
                    if (t < t0) r = 1;
                    else if (t < t1) r = 2;
                    else if (t < t2) r = 3;
                    else if (t < t3) r = 4;
                    else r = 5; // you can split to 6 later if you detect t4
                    reg[i] = r;
                }
                return reg;
            }

            public static double EstimateFps(double[] times)
            {
                if (times == null || times.Length < 2) return double.NaN;
                var dts = new List<double>(times.Length - 1);
                for (int i = 1; i < times.Length; i++)
                {
                    var dt = times[i] - times[i - 1];
                    if (dt > 1e-6) dts.Add(dt);
                }
                if (dts.Count == 0) return double.NaN;
                dts.Sort();
                double medianDt = (dts.Count % 2 == 1) ? dts[dts.Count / 2]
                                  : 0.5 * (dts[dts.Count / 2 - 1] + dts[dts.Count / 2]);
                return medianDt > 0 ? 1.0 / medianDt : double.NaN;
            }
        }

        public sealed class InferenceResponse
        {
            public double t0 { get; set; }
            public double t1 { get; set; }
            public double t3 { get; set; }
            public System.Collections.Generic.List<double> conf { get; set; }
            public string error { get; set; } // if server returns an error
        }

        public sealed class SignalRow
        {
            public double t, I_fixed, I_smooth, slope, dx, dy;
            public SignalRow(double t, double iFix, double iSmooth, double slope, double dx, double dy)
            {
                this.t = t; I_fixed = iFix; I_smooth = iSmooth; this.slope = slope; this.dx = dx; this.dy = dy;
            }
        }

        public static class SignalsBuilder
        {
            public static List<SignalRow> BuildFromIntensityPoints(
                List<IntensityPoint> points,
                int smoothWindowSamples = 11)   // odd number (≈1–2 s if dt≈0.1s)
            {
                if (points == null || points.Count < 3)
                    throw new ArgumentException("Need at least 3 points");

                int n = points.Count;
                var t = new double[n];
                var iFix = new double[n];
                var tx = new double[n];
                var ty = new double[n];
                for (int i = 0; i < n; i++)
                {
                    t[i] = points[i].X;
                    iFix[i] = points[i].Y;
                    tx[i] = points[i].TargetX;
                    ty[i] = points[i].TargetY;
                }

                // median dt
                double dt = 0.1;
                if (n > 1)
                {
                    var diffs = new List<double>(n - 1);
                    for (int i = 1; i < n; i++) diffs.Add(Math.Max(1e-6, t[i] - t[i - 1]));
                    diffs.Sort();
                    dt = diffs[diffs.Count / 2];
                }

                // Centered moving average for I_smooth
                if (smoothWindowSamples < 3) smoothWindowSamples = 3;
                if (smoothWindowSamples % 2 == 0) smoothWindowSamples++;
                int half = smoothWindowSamples / 2;

                var iSmooth = new double[n];
                for (int i = 0; i < n; i++)
                {
                    int a = Math.Max(0, i - half);
                    int b = Math.Min(n - 1, i + half);
                    double sum = 0; int cnt = 0;
                    for (int j = a; j <= b; j++) { sum += iFix[j]; cnt++; }
                    iSmooth[i] = sum / Math.Max(1, cnt);
                }

                // Centered slope d(I_smooth)/dt (handles non-uniform dt)
                var slope = new double[n];
                for (int i = 1; i < n - 1; i++)
                {
                    double dtc = t[i + 1] - t[i - 1];
                    if (dtc <= 0) dtc = 2 * dt;
                    slope[i] = (iSmooth[i + 1] - iSmooth[i - 1]) / dtc;
                }
                slope[0] = slope[1];
                slope[n - 1] = slope[n - 2];

                // dx, dy per step — robust to NaN TargetX/Y
                var dx = new double[n];
                var dy = new double[n];
                dx[0] = 0; dy[0] = 0;

                bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

                for (int i = 1; i < n; i++)
                {
                    if (IsFinite(tx[i]) && IsFinite(ty[i]) && IsFinite(tx[i - 1]) && IsFinite(ty[i - 1]))
                    {
                        dx[i] = tx[i] - tx[i - 1];
                        dy[i] = ty[i] - ty[i - 1];
                    }
                    else
                    {
                        // if either side is NaN/inf, treat as no movement this step
                        dx[i] = 0;
                        dy[i] = 0;
                    }
                }

                // Pack
                var rows = new List<SignalRow>(n);
                for (int i = 0; i < n; i++)
                    rows.Add(new SignalRow(t[i], iFix[i], iSmooth[i], slope[i], dx[i], dy[i]));
                return rows;
            }

            public static string ToCsv(List<SignalRow> rows)
            {
                var inv = CultureInfo.InvariantCulture;
                var sb = new StringBuilder(rows.Count * 40);
                sb.AppendLine("t,I_fixed,I_smooth,slope,dx,dy");
                foreach (var r in rows)
                    sb.AppendFormat(inv, "{0},{1},{2},{3},{4},{5}\n",
                        r.t, r.I_fixed, r.I_smooth, r.slope, r.dx, r.dy);
                return sb.ToString();
            }

            public static List<SignalRow> BuildExportCompatible(List<IntensityPoint> points, int smoothWindowSamples = 11)
            {
                if (points == null || points.Count < 3)
                    throw new ArgumentException("Need at least 3 points");

                // I_fixed
                var iFix = points;

                // I_smooth: zero-phase EMA(τ=1s) to match Export()
                var iSmooth = Smooth.SmoothEmaZeroPhaseTime(iFix, 1.0);

                // slope: trailing LS over 11 samples, then MA(7), taken from I_fixed
                var slope = RollingSlopeAnalysis.ComputeRollingSlopeBySamples(iFix, windowSamples: smoothWindowSamples);
                slope = RollingSlopeAnalysis.SmoothSlopeMA(slope, span: 7);

                // dx, dy: per-step target motion (0 if missing)
                bool hasPos = !(double.IsNaN(iFix[0].TargetX) || double.IsNaN(iFix[0].TargetY));
                var rows = new List<SignalRow>(iFix.Count);
                for (int i = 0; i < iFix.Count; i++)
                {
                    double t = iFix[i].X;
                    double yFix = iFix[i].Y;
                    double ySm = iSmooth[i].Y;

                    double s = slope[Math.Min(i, slope.Count - 1)].S; // may be NaN
                    double dx = 0, dy = 0;
                    if (hasPos && i > 0)
                    {
                        dx = iFix[i].TargetX - iFix[i - 1].TargetX;
                        dy = iFix[i].TargetY - iFix[i - 1].TargetY;
                    }
                    rows.Add(new SignalRow(t, yFix, ySm, s, dx, dy));
                }
                return rows;
            }

            public static string ToCsvExportCompatible(List<SignalRow> rows)
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder(rows.Count * 48);
                sb.AppendLine("t,I_fixed,I_smooth,slope,dx,dy");
                foreach (var r in rows)
                {
                    // slope empty if NaN; dx/dy to 3dp; t/I to 6dp to match exporter
                    string slopeStr = double.IsNaN(r.slope) ? "" : r.slope.ToString("F6", inv);
                    sb.AppendFormat(inv, "{0:F6},{1:F6},{2:F6},{3},{4:F3},{5:F3}\n",
                        r.t, r.I_fixed, r.I_smooth, slopeStr, r.dx, r.dy);
                }
                return sb.ToString();
            }

            // Write a labels_heat.csv with Gaussian targets of configurable width (seconds).
            // t0/t1/t3: event times in seconds. Any null -> channel = zeros.
            public static void WriteLabelsHeatCsv(
                string csvPath,
                List<IntensityPoint> data,
                double? t0, double? t1, double? t3,
                double sigma0Sec = 2.0, double sigma1Sec = 2.0, double sigma3Sec = 2.5,
                bool normalizeEachChannel = true)
            {
                if (data == null || data.Count == 0)
                    throw new ArgumentException("No data");


                const double EPS = 1e-12;

                int n = data.Count;
                var t = new double[n];
                for (int i = 0; i < n; i++) t[i] = data[i].X;

                // Build 3 channels
                var y0 = new double[n];
                var y1 = new double[n];
                var y3 = new double[n];

                Action<double?, double, double[]> paint = (teOpt, sigmaSec, y) =>
                {
                    Array.Clear(y, 0, y.Length);
                    if (!teOpt.HasValue || double.IsNaN(teOpt.Value) || sigmaSec <= 0) return;

                    double te = teOpt.Value;
                    double inv2s2 = 0.5 / Math.Max(EPS, sigmaSec * sigmaSec);
                    double sixS = 6.0 * sigmaSec;

                    double sum = 0.0;
                    for (int i = 0; i < n; i++)
                    {
                        double d = t[i] - te;
                        if (Math.Abs(d) > sixS) continue;        // outside ±6σ → 0

                        double v = Math.Exp(-(d * d) * inv2s2);
                        if (v < EPS) v = 0.0;                    // clamp tiny tails
                        y[i] = v;
                        sum += v;
                    }

                    if (normalizeEachChannel && sum > 0.0)
                    {
                        double inv = 1.0 / sum;
                        for (int i = 0; i < n; i++) y[i] *= inv; // renormalize to sum≈1
                    }
                };

                paint(t0, sigma0Sec, y0);
                paint(t1, sigma1Sec, y1);
                paint(t3, sigma3Sec, y3);

                // Write CSV
                using (var sw = new System.IO.StreamWriter(csvPath, false, Encoding.UTF8))
                {
                    // was: sw.WriteLine("t,y0,y1,y3");
                    sw.WriteLine("t,heat_t0,heat_t1,heat_t3");
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    for (int i = 0; i < n; i++)
                        sw.WriteLine(string.Format(inv, "{0:F6},{1:G17},{2:G17},{3:G17}",
                            t[i], y0[i], y1[i], y3[i]));
                }
            }

            public static (double s0, double s1, double s3) PickSigmas(string videoName)
            {
                // defaults
                double s0 = 1.5, s1 = 2.0, s3 = 3.0;

                if (!string.IsNullOrEmpty(videoName))
                {
                    if (videoName.Contains("RH-13-growth-WN")) s3 = 3.5;
                    if (videoName.Contains("RH-14-growth-WoN")) s1 = 3.0;
                    if (videoName.Contains("RH-17-growth-WoN")) s1 = 3.0;
                    if (videoName.Contains("RH-23-growth-WoN-2")) s0 = 2.0;
                    if (videoName.Contains("RH-25-growth-WoN-2")) s3 = 3.5;
                    if (videoName.Contains("RH-26-growth-WoN")) s3 = 3.5;
                }
                return (s0, s1, s3);
            }
        }

        public static class RheedInferClient
        {
            private static readonly HttpClient _http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };

            public static string BuildCsv(System.Collections.Generic.IEnumerable<SignalRow> rows)
            {
                var sb = new StringBuilder();
                sb.AppendLine("t,I_fixed,I_smooth,slope,dx,dy");
                var inv = CultureInfo.InvariantCulture;
                foreach (var r in rows)
                    sb.AppendLine(string.Format(inv, "{0},{1},{2},{3},{4},{5}", r.t, r.I_fixed, r.I_smooth, r.slope, r.dx, r.dy));
                return sb.ToString();
            }

            // 1) Send raw CSV in body (fastest)
            public static async Task<InferenceResponse> InferCsvAsync(string baseUrl, string csv)
            {
                var url = baseUrl.TrimEnd('/') + "/infer_csv";
                using (var content = new StringContent(csv, Encoding.UTF8, "text/csv"))
                using (var resp = await _http.PostAsync(url, content).ConfigureAwait(false))
                {
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        return new InferenceResponse { error = $"HTTP {(int)resp.StatusCode}: {json}" };
                    return JsonConvert.DeserializeObject<InferenceResponse>(json);
                }
            }

            // Send new content...
            public static async Task<InferenceResponse> InferCsvMultipartAsync(
        string baseUrl,
        string videoName,
        string signalsCsv,
        CancellationToken ct = default)
            {
                var url = baseUrl.TrimEnd('/') + "/infer_csv";

                using (var content = new MultipartFormDataContent())
                {
                    // plain text field: "name"
                    content.Add(new StringContent(videoName ?? string.Empty), "name");

                    // plain text field: "csv" (kept as a form field, not a file)
                    var csvPart = new StringContent(signalsCsv ?? string.Empty, Encoding.UTF8, "text/csv");
                    content.Add(csvPart, "csv");

                    using (var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false))
                    {
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            return new InferenceResponse { error = $"HTTP {(int)resp.StatusCode}: {json}" };

                        return JsonConvert.DeserializeObject<InferenceResponse>(json);
                    }
                }
            }



            // 2) Send as multipart file (if you already wrote signals.csv to disk)
            public static async Task<InferenceResponse> InferFileAsync(string baseUrl, string csvPath)
            {
                var url = baseUrl.TrimEnd('/') + "/infer_csv";
                using (var form = new MultipartFormDataContent())
                using (var fs = File.OpenRead(csvPath))
                {
                    var fileContent = new StreamContent(fs);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
                    form.Add(fileContent, "signals", Path.GetFileName(csvPath));

                    using (var resp = await _http.PostAsync(url, form).ConfigureAwait(false))
                    {
                        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            return new InferenceResponse { error = $"HTTP {(int)resp.StatusCode}: {json}" };
                        return JsonConvert.DeserializeObject<InferenceResponse>(json);
                    }
                }
            }
        }


    }
}
