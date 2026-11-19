using Wasmtime;

namespace DomiBase.IntegrationTests;

/// <summary>
/// Small WebAssembly modules used by the function integration tests. They are
/// compiled from WAT at test time so the tests stay readable and self-contained.
/// </summary>
internal static class TestWasmModules
{
    public const string EchoWat = """
        (module
          (memory (export "memory") 2)
          (func (export "invoke") (param i32 i32) (result i64)
            i32.const 65536
            local.get 0
            local.get 1
            memory.copy
            i64.const 281474976710656
            local.get 1
            i64.extend_i32_u
            i64.or))
        """;

    public const string SpinWat = """
        (module
          (memory (export "memory") 1)
          (func (export "invoke") (param i32 i32) (result i64)
            (loop $l
              br $l)
            i64.const 0))
        """;

    public const string ImportsWat = """
        (module
          (import "env" "log" (func $log))
          (memory (export "memory") 1)
          (func (export "invoke") (param i32 i32) (result i64)
            i64.const 0))
        """;

    public const string LargeMemoryWat = """
        (module
          (memory (export "memory") 2)
          (func (export "invoke") (param i32 i32) (result i64)
            i64.const 0))
        """;

    public static byte[] Compile(string wat) => Module.ConvertText(wat);

    public static string Base64(string wat) => Convert.ToBase64String(Compile(wat));
}
