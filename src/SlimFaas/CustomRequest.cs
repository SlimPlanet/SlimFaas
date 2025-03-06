using MemoryPack;

namespace SlimFaas;

[MemoryPackable]
public partial record struct CustomRequest(List<CustomHeader> Headers, byte[]? Body, string FunctionName, string Path,
    string Method, string Query);

[MemoryPackable]
public partial record struct CustomHeader(string Key, string?[] Values);
