using DotnetVM.Metadata;

namespace DotnetVM.Runtime.Types;

/// <summary>
/// アセンブリの TypeDef を VmType に遅延ロードする。
/// 1 つの TypeDef は必ず 1 つの VmClassType インスタンスに対応 (キャッシュで保証)。
/// アセンブリに存在しない型のうち VM が面を提供するもの (System.Object 等) は
/// ファサード型 (VmIntrinsicType) として合成する。
/// 他アセンブリの型参照 (TypeRef の ResolutionScope = AssemblyRef) は
/// <see cref="Context"/> (VmAssemblyContext) 経由で依存アセンブリに解決する。
/// </summary>
public sealed partial class TypeLoader {
    /// <summary>GenericParam.Flags の変性ビット (ECMA-335 II.22.20)。</summary>
    internal const uint Covariant = 0x0001;
    internal const uint Contravariant = 0x0002;

    private readonly AssemblyImage _image;
    private static readonly object MetadataGate = new();

    /// <summary>ロード対象のアセンブリ。</summary>
    public AssemblyImage Image => _image;

    /// <summary>所属する多アセンブリ コンテキスト (依存解決に使用。未所属 = null)。</summary>
    public VmAssemblyContext? Context { get; internal set; }

    /// <summary>最後に登録されたロードコンテキストの識別子。Unregister 後も型の由来判定に使う。</summary>
    internal Guid? LoadContextIdentity { get; set; }

    private readonly Dictionary<int, VmClassType> _typeDefs = [];
    private readonly Dictionary<string, VmIntrinsicType> _intrinsicTypes = [];
    private readonly Dictionary<int, VmMethod> _methods = [];
    private readonly Dictionary<int, VmField> _fields = [];
    private readonly List<VmClassType> _pendingCompletion = [];
    private readonly HashSet<int> _completedTypeDefs = [];
    /// <summary>完全名 → TypeDef rid の索引 (遅延構築。CoreLib 規模の画像で線形走査を避ける)。</summary>
    private Dictionary<string, int>? _typeDefByFullName;
    /// <summary>型統合辞書 (完全名 → 実型)。Context 配下の画像から解決できた実 TypeDef をキャッシュする
    /// (参照アセンブリ⇔CoreLib のユニフィケーション。否定はキャッシュしない — 遅延ロードで新画像が増えうる)。</summary>
    private readonly Dictionary<string, VmType> _unifiedTypes = [];
    /// <summary>仮想/インターフェースディスパッチ表の構築担当 (遅延生成)。</summary>
    private DispatchMapBuilder? _dispatchBuilder;

    /// <summary>この loader が trusted System.Private.CoreLib 実装画像か (LoadHostCoreLib の
    /// 取得した loader 参照に対して VM 構築時に確定する。タスク 2 hardening:
    /// ファイル名照合でなく参照同一性による trusted marker)。</summary>
    public bool IsTrustedCoreLib { get; internal set; }

    /// <summary>VM が同梱する DotnetVM.CoreLib の実装画像か。
    /// CultureSettings のような VM 専用の host bridge を解決するための marker であり、
    /// guest がロードした同名画像には付与しない。</summary>
    public bool IsTrustedVmCoreLib { get; internal set; }

    public TypeLoader(AssemblyImage image) {
        _image = image;
        InitializeIntrinsicTypes();
    }
}
