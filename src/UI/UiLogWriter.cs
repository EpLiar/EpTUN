using System.Text;

namespace EpTUN;

public sealed class UiLogWriter : TextWriter
{
    private readonly Action<string> _writeLineCallback;
    private readonly StringBuilder _buffer = new();
    private readonly object _sync = new();

    public UiLogWriter(Action<string> writeLineCallback)
    {
        _writeLineCallback = writeLineCallback;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_sync)
        {
            if (value == '\n')
            {
                FlushBufferNoLock();
                return;
            }

            if (value != '\r')
            {
                _buffer.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var c in value)
            {
                if (c == '\n')
                {
                    FlushBufferNoLock();
                }
                else if (c != '\r')
                {
                    _buffer.Append(c);
                }
            }
        }
    }

    public override void WriteLine(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (_sync)
        {
            if (_buffer.Length > 0)
            {
                _buffer.Append(value);
                FlushBufferNoLock();
                return;
            }

            _writeLineCallback(value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_sync)
            {
                FlushBufferNoLock();
            }
        }

        base.Dispose(disposing);
    }

    private void FlushBufferNoLock()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var line = _buffer.ToString();
        _buffer.Clear();
        _writeLineCallback(line);
    }
}

