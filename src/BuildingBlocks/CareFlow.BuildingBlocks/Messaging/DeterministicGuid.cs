using System.Security.Cryptography;
using System.Text;

namespace CareFlow.BuildingBlocks.Messaging;

/// <summary>
/// Name-based GUIDs (RFC 9562 version 5). A consumer that derives its outgoing event IDs from the incoming
/// event ID publishes the <em>same</em> ID when a message is redelivered, so downstream de-duplication catches the repeat.
/// </summary>
public static class DeterministicGuid
{
    public static Guid Create(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        Span<byte> namespaceBytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var input = new byte[16 + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        nameBytes.CopyTo(input, 16);

#pragma warning disable CA5350 // SHA-1 is mandated by RFC 9562 for v5 UUIDs; it is not used for security here.
        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(input, hash);
#pragma warning restore CA5350

        var guid = hash[..16];
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50); // version 5
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80); // RFC variant
        return new Guid(guid, bigEndian: true);
    }
}
