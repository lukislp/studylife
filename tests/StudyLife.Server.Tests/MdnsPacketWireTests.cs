// SPDX-FileCopyrightText: 2026 Lukas Körber
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using StudyLife.Server.Discovery;

namespace StudyLife.Server.Tests;

/// <summary>
/// Wire-format edge cases of MdnsPacket that the contract tests in MdnsTests do not reach: the
/// host label derivation, AAAA records and empty TXT sets in a response, the instance/host
/// question matching, and the hostile-input guards of the name reader (compression pointers,
/// pointer loops, reserved label bits, truncation). The reader runs on every multicast packet
/// on the LAN, so each of these must fail closed rather than throw.
/// </summary>
public class MdnsPacketWireTests
{
    private static MdnsServiceDescription Service(string name = "StudyLife", params string[] txt) =>
        new(name, 443, txt.Length == 0 ? ["version=1.2.3", "url=https://studylife.example.org", "https=true", "path=/"] : txt);

    private static byte[] Header(ushort questions) => [0, 0, 0, 0, (byte)(questions >> 8), (byte)questions, 0, 0, 0, 0, 0, 0];

    private static byte[] Question(ushort type, params string[] labels) =>
        [.. MdnsPacket.EncodeName(labels), (byte)(type >> 8), (byte)type, 0, 1];

    private static List<MdnsQuestion> Ask(ushort type, params string[] labels)
    {
        Assert.True(MdnsPacket.TryReadQuestions([.. Header(1), .. Question(type, labels)], out var questions));
        return questions;
    }

    // ---- host label ------------------------------------------------------------------------

    [Theory]
    [InlineData("StudyLife", "studylife")]
    [InlineData("Lukas' StudyLife 2", "lukas-studylife-2")]
    [InlineData("--Büro  (Main)--", "b-ro-main")]
    [InlineData("!!!", "studylife")]
    [InlineData("äöü", "studylife")]
    public void HostLabel_KeepsOnlyLowercaseAlphanumericsAndSingleDashes(string instanceName, string expected) =>
        Assert.Equal(expected, MdnsPacket.HostLabel(instanceName));

    // ---- response building -----------------------------------------------------------------

    [Fact]
    public void Response_CarriesOneARecordPerIPv4_AndOneAaaaPerIPv6_DeduplicatedAndIgnoringOtherFamilies()
    {
        var v4 = IPAddress.Parse("192.168.1.20");
        var v6 = IPAddress.Parse("2001:db8::20");
        var packet = MdnsPacket.BuildResponse(Service(), [v4, v6, v4, v6], ttlZero: false);

        // PTR (enumeration), PTR (type), SRV, TXT, A, AAAA = 6 answers.
        Assert.Equal(6, (packet[6] << 8) | packet[7]);
        Assert.True(Contains(packet, v4.GetAddressBytes()));
        Assert.True(Contains(packet, v6.GetAddressBytes()));
    }

    [Fact]
    public void Response_WithoutTxtRecords_StillCarriesTheMandatoryEmptyTxtString()
    {
        var empty = new MdnsServiceDescription("StudyLife", 80, []);
        var packet = MdnsPacket.BuildResponse(empty, [], ttlZero: false);

        Assert.Equal(4, (packet[6] << 8) | packet[7]);
        // RFC 6763 section 6.1: an empty TXT record is a single zero-length string, rdlength 1.
        Assert.Equal(new byte[] { 0, 1, 0 }, packet[^3..]);
    }

    [Fact]
    public void Response_RejectsATxtEntryOver255Bytes()
    {
        var service = Service("StudyLife", "url=" + new string('a', 300));
        Assert.Throws<ArgumentException>(() => MdnsPacket.BuildResponse(service, [], ttlZero: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("this-label-is-way-too-long-for-dns-because-it-has-more-than-63-bytes")]
    public void EncodeName_RejectsEmptyAndOverlongLabels(string label) =>
        Assert.Throws<ArgumentException>(() => MdnsPacket.EncodeName([label, "local"]));

    // ---- question matching -----------------------------------------------------------------

    [Theory]
    [InlineData(MdnsPacket.TypeSrv, true)]
    [InlineData(MdnsPacket.TypeTxt, true)]
    [InlineData(MdnsPacket.TypeAny, true)]
    [InlineData(MdnsPacket.TypeA, false)]
    [InlineData(MdnsPacket.TypePtr, false)]
    public void InstanceQuestion_MatchesSrvTxtAndAnyOnly(ushort type, bool expected) =>
        Assert.Equal(expected, MdnsPacket.Matches(Service("Home Lab"), Ask(type, "home lab", "_studylife", "_tcp", "local")));

    [Theory]
    [InlineData(MdnsPacket.TypeA, true)]
    [InlineData(MdnsPacket.TypeAaaa, true)]
    [InlineData(MdnsPacket.TypeAny, true)]
    [InlineData(MdnsPacket.TypeSrv, false)]
    public void HostQuestion_MatchesAddressQueriesOnly(ushort type, bool expected) =>
        Assert.Equal(expected, MdnsPacket.Matches(Service("Home Lab"), Ask(type, "home-lab", "local")));

    [Fact]
    public void ServiceEnumerationQuestion_MatchesPtrAndAny_ButNotTxt()
    {
        var service = Service();
        Assert.True(MdnsPacket.Matches(service, Ask(MdnsPacket.TypePtr, "_services", "_dns-sd", "_udp", "local")));
        Assert.True(MdnsPacket.Matches(service, Ask(MdnsPacket.TypeAny, "_studylife", "_tcp", "local")));
        Assert.False(MdnsPacket.Matches(service, Ask(MdnsPacket.TypeTxt, "_studylife", "_tcp", "local")));
    }

    // ---- hostile or malformed queries ------------------------------------------------------

    [Fact]
    public void CompressedSecondQuestion_IsResolvedThroughThePointer()
    {
        // Q1: _studylife._tcp.local PTR; Q2: "home-lab" + pointer to "local" inside Q1 (offset
        // 12 + 1 + 10 + 1 + 4 = 28), the way real resolvers compress repeated suffixes.
        var q1 = Question(MdnsPacket.TypePtr, "_studylife", "_tcp", "local");
        byte[] q2 = [8, .. "home-lab"u8.ToArray(), 0xC0, 28, 0, (byte)MdnsPacket.TypeA, 0, 1];

        Assert.True(MdnsPacket.TryReadQuestions([.. Header(2), .. q1, .. q2], out var questions));

        Assert.Equal(new[] { "_studylife._tcp.local", "home-lab.local" }, questions.Select(q => q.Name));
        Assert.Equal(MdnsPacket.TypeA, questions[1].Type);
        Assert.True(MdnsPacket.Matches(Service("Home Lab"), [questions[1]]));
    }

    [Fact]
    public void PointerLoop_IsRejectedInsteadOfSpinning()
    {
        // A name that is a pointer to itself (offset 12): the hop limit must end the walk.
        byte[] packet = [.. Header(1), 0xC0, 12, 0, 1, 0, 1];
        Assert.False(MdnsPacket.TryReadQuestions(packet, out var questions));
        Assert.Empty(questions);
    }

    [Fact]
    public void TruncatedPointer_IsRejected()
    {
        byte[] packet = [.. Header(1), 0xC0];
        Assert.False(MdnsPacket.TryReadQuestions(packet, out _));
    }

    [Fact]
    public void PointerPastTheEnd_IsRejected()
    {
        byte[] packet = [.. Header(1), 0xC0, 0xFF, 0, 1, 0, 1];
        Assert.False(MdnsPacket.TryReadQuestions(packet, out _));
    }

    [Theory]
    [InlineData(0x40)] // reserved "extended label" bits 01
    [InlineData(0x80)] // reserved bits 10
    public void ReservedLabelTypes_AreRejected(byte lengthByte)
    {
        byte[] packet = [.. Header(1), lengthByte, (byte)'a', 0, 0, 1, 0, 1];
        Assert.False(MdnsPacket.TryReadQuestions(packet, out _));
    }

    [Fact]
    public void LabelLongerThanThePacket_IsRejected()
    {
        byte[] packet = [.. Header(1), 20, (byte)'a', (byte)'b'];
        Assert.False(MdnsPacket.TryReadQuestions(packet, out _));
    }

    [Fact]
    public void NameWithoutRoomForTypeAndClass_IsRejected()
    {
        byte[] packet = [.. Header(1), .. MdnsPacket.EncodeName(["local"]), 0, 1];
        Assert.False(MdnsPacket.TryReadQuestions(packet, out var questions));
        Assert.Empty(questions);
    }

    [Fact]
    public void QueryWithZeroQuestions_IsValidButMatchesNothing()
    {
        Assert.True(MdnsPacket.TryReadQuestions(Header(0), out var questions));
        Assert.Empty(questions);
        Assert.False(MdnsPacket.Matches(Service(), questions));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }
}
