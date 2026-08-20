using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WortBruecke.Core.Learning;

/// <summary>
/// Projects human-readable curriculum keys onto the positive Int64 key space used by progress storage.
/// SHA-256 keeps the mapping deterministic across processes, runtimes and application upgrades.
/// </summary>
public static class LearningContentId
{
    public static long FromObjective(string objectiveId) => FromNamespacedKey("objective", objectiveId);

    public static long FromExamSection(string examId, string sectionId) =>
        FromNamespacedKey("exam-section", $"{RequireKey(examId, nameof(examId))}/{RequireKey(sectionId, nameof(sectionId))}");

    public static long FromDiagnostic(string diagnosticId) => FromNamespacedKey("diagnostic", diagnosticId);

    private static long FromNamespacedKey(string keyNamespace, string key)
    {
        var canonical = $"lerntype:{keyNamespace}:{RequireKey(key, nameof(key)).ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var value = BinaryPrimitives.ReadInt64BigEndian(hash) & long.MaxValue;
        return value == 0 ? 1 : value;
    }

    private static string RequireKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A stable content key is required.", parameterName);
        }
        return value.Trim();
    }
}
