using NUnit.Framework;
using AgenLink.Terminal;

public class TerminalProtocolTests
{
    [Test]
    public void Encode_Then_DecoderPush_RoundTrips()
    {
        var bytes = TerminalProtocol.Encode(TerminalProtocol.OUTPUT, System.Text.Encoding.UTF8.GetBytes("hi"));
        var dec = new TerminalProtocol.Decoder();
        var frames = dec.Push(bytes, bytes.Length);
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(TerminalProtocol.OUTPUT, frames[0].Type);
        Assert.AreEqual("hi", System.Text.Encoding.UTF8.GetString(frames[0].Payload));
    }

    [Test]
    public void Decoder_Reassembles_Split_Frame()
    {
        var full = TerminalProtocol.Encode(TerminalProtocol.INPUT, new byte[] { 1, 2, 3, 4 });
        var dec = new TerminalProtocol.Decoder();
        Assert.AreEqual(0, dec.Push(full, 3).Count);
        var rest = new byte[full.Length - 3];
        System.Array.Copy(full, 3, rest, 0, rest.Length);
        Assert.AreEqual(1, dec.Push(rest, rest.Length).Count);
    }

    [Test]
    public void EncodeResize_Packs_Cols_Rows_BigEndian()
    {
        var bytes = TerminalProtocol.EncodeResize(120, 40);
        var dec = new TerminalProtocol.Decoder();
        var frames = dec.Push(bytes, bytes.Length);
        Assert.AreEqual(1, frames.Count);
        Assert.AreEqual(TerminalProtocol.RESIZE, frames[0].Type);
        var p = frames[0].Payload;
        Assert.AreEqual(120, (p[0] << 8) | p[1]);
        Assert.AreEqual(40, (p[2] << 8) | p[3]);
    }
}
