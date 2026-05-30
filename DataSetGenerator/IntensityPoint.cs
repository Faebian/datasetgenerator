using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataSetGenerator
{
    public class IntensityPoint
    {
        // Existing semantics: X = time (s), Y = intensity (% or a.u.)
        public double X { get; set; }
        public double Y { get; set; }

        // Optional bounds (used by IR, ignored by RHEED)
        public double? YMin { get; set; }
        public double? YMax { get; set; }

        // New: the lock-on center actually used for this measurement (pixels)
        public double TargetX { get; set; }
        public double TargetY { get; set; }

        public IntensityPoint(double x, double y)
        {
            X = x; Y = y;
            TargetX = double.NaN; TargetY = double.NaN; // stays compatible
        }

        public IntensityPoint(double x, double y, double tx, double ty)
        {
            X = x; Y = y; TargetX = tx; TargetY = ty;
        }

        public IntensityPoint(double x, double y, double ymin, double ymax, double tx, double ty)
        {
            X = x;
            Y = y;
            YMin = ymin;
            YMax = ymax;
            TargetX = tx;
            TargetY = ty;
        }
        // Convenience clone
        public IntensityPoint Clone() => new IntensityPoint(X, Y, TargetX, TargetY);

    }
}
