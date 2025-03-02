﻿using SoulWorker.PasswordExtractor.Extensions;
using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace SoulWorker.PasswordExtractor;

public sealed class Extractor 
{
    #region Public Static Methods

    public static async ValueTask<Extractor> Create(Configuration config)
    {
        var bytes = await File.ReadAllBytesAsync(config.Path);
        return new Extractor(config, bytes);
    }

    #endregion Public Static Methods

    #region Public Methods

    public IDictionary<string, string> FromUnpacked()
    {
        // Find first dataXX declaration in file
        var declOffset = FindAnyDataDeclarationOffset();

        // Find RVA by file offset
        var declAddress = _headers.AddressFromOffset(declOffset);

        var usageOffset = FindDataUsageOffset(declAddress);
        var usageAddress = _headers.AddressFromOffset(usageOffset);

        Debug.WriteLine($"usageAddress {usageAddress}");

        var namesBeginOffset = GetBackwardSnapshotOffset(usageOffset);

        Debug.WriteLine($"namesBeginOffset address {_headers.AddressFromOffset(namesBeginOffset):X}");
        
        var namesEndOffset = GetForwardSnapshotOffset(usageOffset);

        Debug.WriteLine($"namesEndOffset address {_headers.AddressFromOffset(namesEndOffset):X}");
        Debug.WriteLine($"Names method length: {namesEndOffset - namesBeginOffset}");

        var passwordsEndOffset = GetForwardSnapshotOffset(namesEndOffset);

        Debug.WriteLine($"Passwords method length: {passwordsEndOffset - namesEndOffset}");

        var names = _extractor.GetStringsNames(namesBeginOffset, namesEndOffset).ToArray();
        var passwords = _extractor.GetStringsPasswords(namesEndOffset, passwordsEndOffset).ToArray();

        if (names.Length != passwords.Length)
            throw new ApplicationException($"Names count ({names.Length}) != passwords count ({passwords.Length})");

        return names.Zip(passwords).ToDictionary(k => k.First, v => v.Second);
    }

    #endregion Public Methods

    #region Private Methods

    private int FindAnyDataDeclarationOffset()
    {
        for (var span = _memory.Span; !span.IsEmpty; span = span[1..])
        {
            if (!Validator.Check(span)) continue;

            var offset = _memory.Length - span.Length;
            Debug.WriteLine($"Data ({Validator.GetFullName(span)}) declaration offset: {offset:X}");

            return offset;
        }

        throw new ApplicationException("Offset not found");
    }

    private int FindDataUsageOffset(int address)
    {
        var bytes = CreateSequence(address);
        for (var span = _memory.Span; !span.IsEmpty; span = span[1..])
        {
            if (!span.StartsWith(bytes)) continue;

            var offset = _memory.Length - span.Length;
            Debug.WriteLine($"Data usage offset: {offset:X}");

            return offset;
        }

        throw new ApplicationException("Offset not found");
    }

    private int GetBackwardSnapshotOffset(int offset)
    {
        var span = _memory.Span[..offset];

        do
        {
            span = span[..^1];
        }
        while (span.Length > 2 && (span[^2] != (byte)AssemblyOpcode.Ret || span[^1] != (byte)AssemblyOpcode.Align));

        return span.Length;
    }

    private int GetForwardSnapshotOffset(int offset)
    {
        var span = _memory.Span[(offset + 1)..];

        do
        {
            span = span[1..];
        }
        while (span.Length > 2 && (span[0] != (byte)AssemblyOpcode.Ret || span[1] != (byte)AssemblyOpcode.Align));

        return offset + (_memory.Length - (offset + span.Length));
    }

    #endregion Private Methods

    #region Private Static Methods

    private static byte[] CreateSequence(int address)
    {
        var bytes = new List<byte> { (byte)AssemblyOpcode.Push };

        bytes.AddRange(BitConverter.GetBytes(address));

        return bytes.ToArray();
    }

    #endregion Private Static Methods

    #region Private Constructors

    private Extractor(Configuration config, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        _headers = new PEHeaders(stream);
        
        _memory = bytes.AsMemory();
        _extractor = new(config, _memory, _headers);
    }

    #endregion Private Constructors

    #region Private Fields

    private readonly PEHeaders _headers;
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly StringExtractor _extractor;

    #endregion Private Fields
}
