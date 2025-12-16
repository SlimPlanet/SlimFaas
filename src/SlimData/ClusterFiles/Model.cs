using System.IO;

namespace SlimData.ClusterFiles;

public sealed record FilePutResult(string Sha256Hex, string ContentType, long Length);

public sealed record FilePullResult(Stream? Stream); // null if not found