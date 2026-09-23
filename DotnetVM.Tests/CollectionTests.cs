using System.Reflection;
using DotnetVM.Host;
using Xunit;

namespace DotnetVM.Tests;

/// <summary>
/// CoreLib の標準コレクションを使うゲストコードを CLR と VM で突合する。
/// List / Dictionary の基本操作と、インターフェース経由の列挙を個別に検証する。
/// </summary>
public class CollectionTests {
    private const string Source = """
        using System.Collections.Generic;

        namespace Vm {
            public static class Collections {
                public static string ListMutations() {
                    var list = new List<int> { 3, 1, 2 };
                    list.Insert(1, 7);
                    list[0] = 4;
                    var removed = list.Remove(7);
                    list.RemoveAt(1);
                    list.Add(5);

                    var result = "" + list.Count;
                    result += ":" + removed;
                    result += ":" + list.Contains(5);
                    result += ":" + list.IndexOf(2);
                    result += ":";
                    for (var i = 0; i < list.Count; i++) {
                        if (i > 0) result += ",";
                        result += list[i];
                    }
                    return result;
                }

                public static string ListForeachThroughInterface() {
                    IEnumerable<int> values = new List<int> { 2, 3, 5 };
                    var sum = 0;
                    var joined = "";
                    foreach (var value in values) {
                        sum += value;
                        joined += value + ",";
                    }
                    return sum + ":" + joined;
                }

                public static string DictionaryLookupAndMutation() {
                    var values = new Dictionary<string, int> { ["a"] = 1 };
                    values.Add("b", 2);
                    values["a"] = 4;
                    var found = values.TryGetValue("b", out var foundValue);
                    var missing = values.TryGetValue("missing", out var missingValue);
                    var removed = values.Remove("a");

                    var result = "" + values.Count;
                    result += ":" + values.ContainsKey("a");
                    result += ":" + values["b"];
                    result += ":" + found + ":" + foundValue;
                    result += ":" + missing + ":" + missingValue;
                    result += ":" + removed;
                    return result;
                }

                public static string ArrayInterfaces() {
                    var array = new[] { 2, 3, 5 };
                    ICollection<int> collection = array;
                    IReadOnlyCollection<int> readOnlyCollection = array;
                    IEnumerable<int> sequence = array;
                    var sum = 0;
                    foreach (var value in sequence) sum += value;
                    return collection.Count + ":" + readOnlyCollection.Count + ":" + sum;
                }
            }
        }
        """;

    private static readonly (Assembly Clr, byte[] Bytes) Compiled = CompileOnce();

    private static (Assembly Clr, byte[] Bytes) CompileOnce() {
        var bytes = TestAssemblyCompiler.CompileToBytes(Source, "CollectionTestsAsm");
        return (Assembly.Load(bytes), bytes);
    }

    private static string RunClr(string method) =>
        (string)Compiled.Clr.GetType("Vm.Collections")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;

    private static string RunVm(string method) {
        using var vm = new VirtualMachine(new VmHostOptions { LoadHostCoreLib = true });
        using var stream = new MemoryStream(Compiled.Bytes);
        vm.LoadAssembly(stream);
        return (string)vm.Invoke("Vm.Collections", method)!;
    }

    private static void AssertMatchesClr(string method, string expected) {
        Assert.Equal(expected, RunClr(method));
        Assert.Equal(RunClr(method), RunVm(method));
    }

    [Fact]
    public void List_MutationsAndQueries_MatchClr() =>
        AssertMatchesClr("ListMutations", "3:True:True:1:4,2,5");

    [Fact]
    public void List_ForeachThroughIEnumerable_MatchesClr() =>
        AssertMatchesClr("ListForeachThroughInterface", "10:2,3,5,");

    [Fact]
    public void Dictionary_LookupAndMutation_MatchClr() =>
        AssertMatchesClr("DictionaryLookupAndMutation", "1:False:2:True:2:False:0:True");

    [Fact]
    public void Array_GenericCollectionInterfaces_MatchClr() =>
        AssertMatchesClr("ArrayInterfaces", "3:3:10");
}
