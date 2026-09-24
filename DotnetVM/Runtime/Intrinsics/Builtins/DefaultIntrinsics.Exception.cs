using DotnetVM.Metadata;
using DotnetVM.Policy;
using DotnetVM.Runtime.Execution;
using DotnetVM.Runtime.Objects;
using DotnetVM.Runtime.Types;

namespace DotnetVM.Runtime.Intrinsics.Builtins;

public static partial class DefaultIntrinsics {
    // ---- System.Exception (ファサード階層の共通面) ----

    /// <summary>CLR 互換の既定 ToString。Exception 派生クラスは「型名: メッセージ」形式。</summary>
    private static string DefaultToString(IntrinsicContext ctx, VmClassInstance ci) {
        if (!DerivesFromException(ci.ClassType))
            return ci.ClassType.FullName;
        var message = IntrinsicContext.GetExceptionMessage(ci);
        return FormatExceptionText(ci.ClassType.FullName, message);
    }

    private static string FormatExceptionText(string typeName, VmString? message) {
        var text = message?.Value;
        return string.IsNullOrEmpty(text) ? typeName : $"{typeName}: {text}";
    }

    /// <summary>クラスが Exception ファサードを基底に持つか (ゲスト例外クラスの判定)。</summary>
    private static bool DerivesFromException(VmType type) {
        for (var t = (VmType?)type; t is not null; t = t.BaseType)
            if (t.FullName == "System.Exception")
                return true;
        return false;
    }

    /// <summary>例外メッセージの取得 (VmExceptionObject / ゲスト Exception 派生クラスの両方)。</summary>
    private static VmString? GetMessageOf(in StackSlot slot) => slot.ObjectValue switch {
        VmExceptionObject e => e.Message,
        VmClassInstance ci when DerivesFromException(ci.ClassType) => IntrinsicContext.GetExceptionMessage(ci),
        _ => null,
    };

    private static Exception BadReceiverException(Args s) =>
        new InvalidOperationException($"例外 intrinsic のレシーバが不正です: {DescribeReceiver(s[0])}");

    private static string DescribeReceiver(in StackSlot slot) => slot.ObjectValue switch {
        null => "null",
        var o => o.GetType().Name,
    };

    private static void RegisterExceptions(IntrinsicRegistry r) {
        const string T = "System.Exception";
        void Instance(string name, int ps, IntrinsicImpl impl) =>
            r.Register(IntrinsicKey.Instance(T, name, ps), impl);

        // .ctor: メッセージの記録。VmExceptionObject には直接、ゲスト派生クラスには例外メッセージ表へ
        Instance(".ctor", 0, static (_, a) => {
            var s = new Args(a);
            SetMessage(s[0], null);
            return null;
        });
        Instance(".ctor", 1, static (_, a) => {
            var s = new Args(a);
            SetMessage(s[0], s[1].ObjectValue as VmString);
            return null;
        });
        Instance("get_Message", 0, static (ctx, a) => {
            var s = new Args(a);
            if (s[0].ObjectValue is not (VmExceptionObject or VmClassInstance))
                throw BadReceiverException(s);
            var message = s[0].ObjectValue switch {
                VmExceptionObject e => e.Message,
                VmClassInstance ci when DerivesFromException(ci.ClassType) =>
                    IntrinsicContext.GetExceptionMessage(ci),
                _ => null,
            };
            // CLR 互換: メッセージ未設定なら既定文言面。VM 内部例外 (ゼロ除算等) と
            // パラメータなし .ctor の両方がここを通るため、既定文言をホスト CLR の
            // 例外型に委譲して再現する (Format 等と同じ culture 依存テキストのプロキシ委譲)
            var typeName = s[0].ObjectValue switch {
                VmExceptionObject e => e.Type.FullName,
                VmClassInstance ci => ci.ClassType.FullName,
                _ => throw new InvalidOperationException("unreachable"),
            };
            var text = message is not null && message.Value.Length > 0
                ? message.Value
                : HostDefaultMessageOrDefault(typeName);
            return StackSlot.OfObject(ctx.MakeString(text));
        });
        Instance("ToString", 0, static (ctx, a) => {
            var s = new Args(a);
            return s[0].ObjectValue switch {
                VmExceptionObject e => StackSlot.OfObject(ctx.MakeString(FormatExceptionText(e.Type.FullName, e.Message))),
                VmClassInstance ci => StackSlot.OfObject(ctx.MakeString(DefaultToString(ctx, ci))),
                _ => throw BadReceiverException(s),
            };
        });
    }

    /// <summary>Exception::.ctor のメッセージ記録 (レシーバの種類で保存先を選ぶ)。</summary>
    private static void SetMessage(in StackSlot receiver, VmString? message) {
        switch (receiver.ObjectValue) {
            case VmExceptionObject e:
                e.Message = message;
                break;
            case VmClassInstance ci:
                IntrinsicContext.SetExceptionMessage(ci, message);
                break;
            default:
                throw new InvalidOperationException($"例外 .ctor のレシーバが不正です: {DescribeReceiver(receiver)}");
        }
    }

    /// <summary>メッセージ未設定の例外の CLR 既定文言 (例: DivideByZeroException →
    /// "Attempted to divide by zero.")。culture 依存テキスト面のためホスト CLR の例外型を
    /// インスタンス化して委譲する。ホストに対応型がない (ゲスト派生型等) / 生成できない場合は
    /// CLR の汎用文言 "Exception of type 'X' was thrown." にフォールバックする。</summary>
    private static string HostDefaultMessageOrDefault(string exceptionTypeName) {
        const string generic = "Exception of type '{0}' was thrown.";
        try {
            var hostType = Type.GetType($"{exceptionTypeName}, System.Private.CoreLib", throwOnError: false) ??
                Type.GetType($"{exceptionTypeName}, System.Runtime", throwOnError: false);
            if (hostType is null || !typeof(Exception).IsAssignableFrom(hostType))
                return string.Format(generic, exceptionTypeName);
            return ((Exception)Activator.CreateInstance(hostType)!).Message;
        } catch {
            return string.Format(generic, exceptionTypeName);
        }
    }
}
