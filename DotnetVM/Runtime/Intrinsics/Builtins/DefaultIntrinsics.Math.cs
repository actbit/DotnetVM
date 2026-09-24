using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    // ---- System.Math ----

    private static void RegisterMath(IntrinsicRegistry r) {
        const string T = "System.Math";
        void S(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Static(T, name, ps), impl);

        S("Abs", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Abs(s.Int32(0))),
                StackKind.Int64 => StackSlot.OfInt64(Math.Abs(s.Int64(0))),
                _ => StackSlot.OfFloat(Math.Abs(s.Float(0))),
            };
        });
        S("Max", 2, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Max(s.Int32(0), s.Int32(1))),
                StackKind.Int64 => StackSlot.OfInt64(Math.Max(s.Int64(0), s.Int64(1))),
                _ => StackSlot.OfFloat(Math.Max(s.Float(0), s.Float(1))),
            };
        });
        S("Min", 2, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Min(s.Int32(0), s.Int32(1))),
                StackKind.Int64 => StackSlot.OfInt64(Math.Min(s.Int64(0), s.Int64(1))),
                _ => StackSlot.OfFloat(Math.Min(s.Float(0), s.Float(1))),
            };
        });
        S("Sqrt", 1, static (_, a) => StackSlot.OfFloat(Math.Sqrt(new Args(a).Float(0))));
        S("Pow", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfFloat(Math.Pow(s.Float(0), s.Float(1)));
        });
        S("Floor", 1, static (_, a) => StackSlot.OfFloat(Math.Floor(new Args(a).Float(0))));
        S("Ceiling", 1, static (_, a) => StackSlot.OfFloat(Math.Ceiling(new Args(a).Float(0))));
        S("Round", 1, static (_, a) => StackSlot.OfFloat(Math.Round(new Args(a).Float(0), MidpointRounding.ToEven)));
        S("Truncate", 1, static (_, a) => StackSlot.OfFloat(Math.Truncate(new Args(a).Float(0))));
        S("Sign", 1, static (_, a) => {
            var s = new Args(a);
            return s[0].Kind switch {
                StackKind.Int32 => StackSlot.OfInt32(Math.Sign(s.Int32(0))),
                StackKind.Int64 => StackSlot.OfInt32(Math.Sign(s.Int64(0))),
                _ => StackSlot.OfInt32(Math.Sign(s.Float(0))),
            };
        });
        S("Sin", 1, static (_, a) => StackSlot.OfFloat(Math.Sin(new Args(a).Float(0))));
        S("Cos", 1, static (_, a) => StackSlot.OfFloat(Math.Cos(new Args(a).Float(0))));
        S("Tan", 1, static (_, a) => StackSlot.OfFloat(Math.Tan(new Args(a).Float(0))));
        S("Asin", 1, static (_, a) => StackSlot.OfFloat(Math.Asin(new Args(a).Float(0))));
        S("Acos", 1, static (_, a) => StackSlot.OfFloat(Math.Acos(new Args(a).Float(0))));
        S("Atan", 1, static (_, a) => StackSlot.OfFloat(Math.Atan(new Args(a).Float(0))));
        S("Atan2", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfFloat(Math.Atan2(s.Float(0), s.Float(1)));
        });
        S("Exp", 1, static (_, a) => StackSlot.OfFloat(Math.Exp(new Args(a).Float(0))));
        S("Log", 1, static (_, a) => StackSlot.OfFloat(Math.Log(new Args(a).Float(0))));
        S("Log", 2, static (_, a) => {
            var s = new Args(a);
            return StackSlot.OfFloat(Math.Log(s.Float(0), s.Float(1)));
        });
        S("Log10", 1, static (_, a) => StackSlot.OfFloat(Math.Log10(new Args(a).Float(0))));
    }
}
