namespace DotnetVM.Metadata;

/// <summary>ECMA-335 メタデータテーブル番号 (II.22)。</summary>
public enum TableKind : byte {
    Module = 0x00,
    TypeRef = 0x01,
    TypeDef = 0x02,
    FieldPtr = 0x03,
    Field = 0x04,
    MethodPtr = 0x05,
    MethodDef = 0x06,
    ParamPtr = 0x07,
    Param = 0x08,
    InterfaceImpl = 0x09,
    MemberRef = 0x0A,
    Constant = 0x0B,
    CustomAttribute = 0x0C,
    FieldMarshal = 0x0D,
    DeclSecurity = 0x0E,
    ClassLayout = 0x0F,
    FieldLayout = 0x10,
    StandAloneSig = 0x11,
    EventMap = 0x12,
    EventPtr = 0x13,
    Event = 0x14,
    PropertyMap = 0x15,
    PropertyPtr = 0x16,
    Property = 0x17,
    MethodSemantics = 0x18,
    MethodImpl = 0x19,
    ModuleRef = 0x1A,
    TypeSpec = 0x1B,
    ImplMap = 0x1C,
    FieldRVA = 0x1D,
    EncLog = 0x1E,
    EncMap = 0x1F,
    Assembly = 0x20,
    AssemblyProcessor = 0x21,
    AssemblyOS = 0x22,
    AssemblyRef = 0x23,
    AssemblyRefProcessor = 0x24,
    AssemblyRefOS = 0x25,
    File = 0x26,
    ExportedType = 0x27,
    ManifestResource = 0x28,
    NestedClass = 0x29,
    GenericParam = 0x2A,
    MethodSpec = 0x2B,
    GenericParamConstraint = 0x2C,
}

/// <summary>coded index グループ (II.24.2.6)。ターゲットテーブルの集合を定義する。</summary>
public enum CodedIndexKind : byte {
    TypeDefOrRef,          // TypeDef, TypeRef, TypeSpec
    HasConstant,           // Field, Param, Property
    HasCustomAttribute,    // MethodDef, Field, TypeRef, TypeDef, Param, InterfaceImpl, MemberRef, Module, DeclSecurity, Property, Event, StandAloneSig, ModuleRef, TypeSpec, Assembly, AssemblyRef, File, ExportedType, ManifestResource, GenericParam, GenericParamConstraint, MethodSpec
    HasFieldMarshal,       // Field, Param
    HasDeclSecurity,       // TypeDef, MethodDef, Assembly
    MemberRefParent,       // TypeDef, TypeRef, ModuleRef, MethodDef, TypeSpec
    HasSemantics,          // Event, Property
    MethodDefOrRef,        // MethodDef, MemberRef
    MemberForwarded,       // Field, MethodDef
    Implementation,        // File, AssemblyRef, ExportedType
    CustomAttributeType,   // (未使用), (未使用), MethodDef, MemberRef, (未使用)
    ResolutionScope,       // Module, ModuleRef, AssemblyRef, TypeRef
    TypeOrMethodDef,       // TypeDef, MethodDef
}

/// <summary>テーブル列の種別。</summary>
public enum ColumnKind : byte {
    U1, U2, U4,     // 固定幅定数
    String,         // #Strings インデックス
    Guid,           // #GUID インデックス
    Blob,           // #Blob インデックス
    Table,          // 単一テーブルの行インデックス
    Coded,          // coded index
}

/// <summary>列の定義。Fixed か Kind を使う。</summary>
public readonly record struct ColumnDef(string Name, ColumnKind Kind, TableKind Target = 0,
                                        CodedIndexKind Coded = 0);

/// <summary>
/// VM が解釈する必要のあるテーブルのスキーマ定義 (II.22)。
/// 未掲載テーブルは行サイズの計算のためだけに既定の空スキーマで読み飛ばす。
/// </summary>
public static class TableSchema {
    /// <summary>coded index の「未使用タグ」を表す番兵。</summary>
    private const TableKind None = (TableKind)0xFF;

    private static readonly Dictionary<TableKind, ColumnDef[]> s_schema = new() {
        [TableKind.Module] = new[] {
            new ColumnDef("Generation", ColumnKind.U2),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Mvid", ColumnKind.Guid),
            new ColumnDef("EncId", ColumnKind.Guid),
            new ColumnDef("EncBaseId", ColumnKind.Guid),
        },
        [TableKind.TypeRef] = new[] {
            new ColumnDef("ResolutionScope", ColumnKind.Coded, Coded: CodedIndexKind.ResolutionScope),
            new ColumnDef("TypeName", ColumnKind.String),
            new ColumnDef("TypeNamespace", ColumnKind.String),
        },
        [TableKind.TypeDef] = new[] {
            new ColumnDef("Flags", ColumnKind.U4),
            new ColumnDef("TypeName", ColumnKind.String),
            new ColumnDef("TypeNamespace", ColumnKind.String),
            new ColumnDef("Extends", ColumnKind.Coded, Coded: CodedIndexKind.TypeDefOrRef),
            new ColumnDef("FieldList", ColumnKind.Table, Target: TableKind.Field),
            new ColumnDef("MethodList", ColumnKind.Table, Target: TableKind.MethodDef),
        },
        [TableKind.Field] = new[] {
            new ColumnDef("Flags", ColumnKind.U2),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Signature", ColumnKind.Blob),
        },
        [TableKind.MethodDef] = new[] {
            new ColumnDef("RVA", ColumnKind.U4),
            new ColumnDef("ImplFlags", ColumnKind.U2),
            new ColumnDef("Flags", ColumnKind.U2),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Signature", ColumnKind.Blob),
            new ColumnDef("ParamList", ColumnKind.Table, Target: TableKind.Param),
        },
        [TableKind.Param] = new[] {
            new ColumnDef("Flags", ColumnKind.U2),
            new ColumnDef("Sequence", ColumnKind.U2),
            new ColumnDef("Name", ColumnKind.String),
        },
        [TableKind.InterfaceImpl] = new[] {
            new ColumnDef("Class", ColumnKind.Table, Target: TableKind.TypeDef),
            new ColumnDef("Interface", ColumnKind.Coded, Coded: CodedIndexKind.TypeDefOrRef),
        },
        [TableKind.MemberRef] = new[] {
            new ColumnDef("Class", ColumnKind.Coded, Coded: CodedIndexKind.MemberRefParent),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Signature", ColumnKind.Blob),
        },
        [TableKind.Constant] = new[] {
            new ColumnDef("Type", ColumnKind.U1),
            new ColumnDef("Padding", ColumnKind.U1),
            new ColumnDef("Parent", ColumnKind.Coded, Coded: CodedIndexKind.HasConstant),
            new ColumnDef("Value", ColumnKind.Blob),
        },
        [TableKind.CustomAttribute] = new[] {
            new ColumnDef("Parent", ColumnKind.Coded, Coded: CodedIndexKind.HasCustomAttribute),
            new ColumnDef("Type", ColumnKind.Coded, Coded: CodedIndexKind.CustomAttributeType),
            new ColumnDef("Value", ColumnKind.Blob),
        },
        [TableKind.FieldMarshal] = new[] {
            new ColumnDef("Parent", ColumnKind.Coded, Coded: CodedIndexKind.HasFieldMarshal),
            new ColumnDef("NativeType", ColumnKind.Blob),
        },
        [TableKind.DeclSecurity] = new[] {
            new ColumnDef("Action", ColumnKind.U2),
            new ColumnDef("Parent", ColumnKind.Coded, Coded: CodedIndexKind.HasDeclSecurity),
            new ColumnDef("PermissionSet", ColumnKind.Blob),
        },
        [TableKind.ClassLayout] = new[] {
            new ColumnDef("PackingSize", ColumnKind.U2),
            new ColumnDef("ClassSize", ColumnKind.U4),
            new ColumnDef("Parent", ColumnKind.Table, Target: TableKind.TypeDef),
        },
        [TableKind.FieldLayout] = new[] {
            new ColumnDef("Offset", ColumnKind.U4),
            new ColumnDef("Field", ColumnKind.Table, Target: TableKind.Field),
        },
        [TableKind.StandAloneSig] = new[] {
            new ColumnDef("Signature", ColumnKind.Blob),
        },
        [TableKind.EventMap] = new[] {
            new ColumnDef("Parent", ColumnKind.Table, Target: TableKind.TypeDef),
            new ColumnDef("EventList", ColumnKind.Table, Target: TableKind.Event),
        },
        [TableKind.Event] = new[] {
            new ColumnDef("EventFlags", ColumnKind.U2),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("EventType", ColumnKind.Coded, Coded: CodedIndexKind.TypeDefOrRef),
        },
        [TableKind.PropertyMap] = new[] {
            new ColumnDef("Parent", ColumnKind.Table, Target: TableKind.TypeDef),
            new ColumnDef("PropertyList", ColumnKind.Table, Target: TableKind.Property),
        },
        [TableKind.Property] = new[] {
            new ColumnDef("Flags", ColumnKind.U2),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Type", ColumnKind.Blob),
        },
        [TableKind.MethodSemantics] = new[] {
            new ColumnDef("Semantics", ColumnKind.U2),
            new ColumnDef("Method", ColumnKind.Table, Target: TableKind.MethodDef),
            new ColumnDef("Association", ColumnKind.Coded, Coded: CodedIndexKind.HasSemantics),
        },
        [TableKind.MethodImpl] = new[] {
            new ColumnDef("Class", ColumnKind.Table, Target: TableKind.TypeDef),
            new ColumnDef("MethodBody", ColumnKind.Coded, Coded: CodedIndexKind.MethodDefOrRef),
            new ColumnDef("MethodDeclaration", ColumnKind.Coded, Coded: CodedIndexKind.MethodDefOrRef),
        },
        [TableKind.ModuleRef] = new[] {
            new ColumnDef("Name", ColumnKind.String),
        },
        [TableKind.TypeSpec] = new[] {
            new ColumnDef("Signature", ColumnKind.Blob),
        },
        [TableKind.ImplMap] = new[] {
            new ColumnDef("MappingFlags", ColumnKind.U2),
            new ColumnDef("MemberForwarded", ColumnKind.Coded, Coded: CodedIndexKind.MemberForwarded),
            new ColumnDef("ImportName", ColumnKind.String),
            new ColumnDef("ImportScope", ColumnKind.Table, Target: TableKind.ModuleRef),
        },
        [TableKind.FieldRVA] = new[] {
            new ColumnDef("RVA", ColumnKind.U4),
            new ColumnDef("Field", ColumnKind.Table, Target: TableKind.Field),
        },
        [TableKind.Assembly] = new[] {
            new ColumnDef("HashAlgId", ColumnKind.U4),
            new ColumnDef("MajorVersion", ColumnKind.U2),
            new ColumnDef("MinorVersion", ColumnKind.U2),
            new ColumnDef("BuildNumber", ColumnKind.U2),
            new ColumnDef("RevisionNumber", ColumnKind.U2),
            new ColumnDef("Flags", ColumnKind.U4),
            new ColumnDef("PublicKey", ColumnKind.Blob),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Culture", ColumnKind.String),
        },
        [TableKind.AssemblyRef] = new[] {
            new ColumnDef("MajorVersion", ColumnKind.U2),
            new ColumnDef("MinorVersion", ColumnKind.U2),
            new ColumnDef("BuildNumber", ColumnKind.U2),
            new ColumnDef("RevisionNumber", ColumnKind.U2),
            new ColumnDef("Flags", ColumnKind.U4),
            new ColumnDef("PublicKeyOrToken", ColumnKind.Blob),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Culture", ColumnKind.String),
            new ColumnDef("HashValue", ColumnKind.Blob),
        },
        [TableKind.File] = new[] {
            new ColumnDef("Flags", ColumnKind.U4),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("HashValue", ColumnKind.Blob),
        },
        [TableKind.ExportedType] = new[] {
            new ColumnDef("Flags", ColumnKind.U4),
            new ColumnDef("TypeDefId", ColumnKind.U4),
            new ColumnDef("TypeName", ColumnKind.String),
            new ColumnDef("TypeNamespace", ColumnKind.String),
            new ColumnDef("Implementation", ColumnKind.Coded, Coded: CodedIndexKind.Implementation),
        },
        [TableKind.ManifestResource] = new[] {
            new ColumnDef("Offset", ColumnKind.U4),
            new ColumnDef("Flags", ColumnKind.U4),
            new ColumnDef("Name", ColumnKind.String),
            new ColumnDef("Implementation", ColumnKind.Coded, Coded: CodedIndexKind.Implementation),
        },
        [TableKind.NestedClass] = new[] {
            new ColumnDef("NestedClass", ColumnKind.Table, Target: TableKind.TypeDef),
            new ColumnDef("EnclosingClass", ColumnKind.Table, Target: TableKind.TypeDef),
        },
        [TableKind.GenericParam] = new[] {
            new ColumnDef("Number", ColumnKind.U2),
            new ColumnDef("Flags", ColumnKind.U2),
            new ColumnDef("Owner", ColumnKind.Coded, Coded: CodedIndexKind.TypeOrMethodDef),
            new ColumnDef("Name", ColumnKind.String),
        },
        [TableKind.MethodSpec] = new[] {
            new ColumnDef("Method", ColumnKind.Coded, Coded: CodedIndexKind.MethodDefOrRef),
            new ColumnDef("Instantiation", ColumnKind.Blob),
        },
        [TableKind.GenericParamConstraint] = new[] {
            new ColumnDef("Owner", ColumnKind.Table, Target: TableKind.GenericParam),
            new ColumnDef("Constraint", ColumnKind.Coded, Coded: CodedIndexKind.TypeDefOrRef),
        },
    };

    /// <summary>スキーマ未定義のテーブルの既定 (列なし = 行サイズ 0)。</summary>
    public static IReadOnlyList<ColumnDef> GetColumns(TableKind table) =>
        s_schema.TryGetValue(table, out var columns) ? columns : Array.Empty<ColumnDef>();

    /// <summary>coded index グループのターゲットテーブル一覧 (未使用タグは 0 個の配列)。</summary>
    public static TableKind[] GetCodedTargets(CodedIndexKind kind) => kind switch {
        CodedIndexKind.TypeDefOrRef => new[] { TableKind.TypeDef, TableKind.TypeRef, TableKind.TypeSpec },
        CodedIndexKind.HasConstant => new[] { TableKind.Field, TableKind.Param, TableKind.Property },
        CodedIndexKind.HasCustomAttribute => new[] {
            TableKind.MethodDef, TableKind.Field, TableKind.TypeRef, TableKind.TypeDef,
            TableKind.Param, TableKind.InterfaceImpl, TableKind.MemberRef, TableKind.Module,
            TableKind.DeclSecurity, TableKind.Property, TableKind.Event, TableKind.StandAloneSig,
            TableKind.ModuleRef, TableKind.TypeSpec, TableKind.Assembly, TableKind.AssemblyRef,
            TableKind.File, TableKind.ExportedType, TableKind.ManifestResource,
            TableKind.GenericParam, TableKind.GenericParamConstraint, TableKind.MethodSpec,
        },
        CodedIndexKind.HasFieldMarshal => new[] { TableKind.Field, TableKind.Param },
        CodedIndexKind.HasDeclSecurity => new[] { TableKind.TypeDef, TableKind.MethodDef, TableKind.Assembly },
        CodedIndexKind.MemberRefParent => new[] { TableKind.TypeDef, TableKind.TypeRef, TableKind.ModuleRef, TableKind.MethodDef, TableKind.TypeSpec },
        CodedIndexKind.HasSemantics => new[] { TableKind.Event, TableKind.Property },
        CodedIndexKind.MethodDefOrRef => new[] { TableKind.MethodDef, TableKind.MemberRef },
        CodedIndexKind.MemberForwarded => new[] { TableKind.Field, TableKind.MethodDef },
        CodedIndexKind.Implementation => new[] { TableKind.File, TableKind.AssemblyRef, TableKind.ExportedType },
        CodedIndexKind.CustomAttributeType => new[] { None, None, TableKind.MethodDef, TableKind.MemberRef, None },
        CodedIndexKind.ResolutionScope => new[] { TableKind.Module, TableKind.ModuleRef, TableKind.AssemblyRef, TableKind.TypeRef },
        CodedIndexKind.TypeOrMethodDef => new[] { TableKind.TypeDef, TableKind.MethodDef },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };


}
