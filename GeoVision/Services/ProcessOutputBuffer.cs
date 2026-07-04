using System.Text;

namespace GeoVision.Services
{
    internal sealed class ProcessOutputBuffer
    {
        private const int DefaultMaxChars = 64 * 1024;

        private readonly object _syncRoot = new();
        private readonly StringBuilder _buffer = new();
        private readonly int _maxChars;

        public ProcessOutputBuffer(int maxChars = DefaultMaxChars)
        {
            _maxChars = Math.Max(4096, maxChars);
        }

        public int Length
        {
            get
            {
                lock (_syncRoot)
                    return _buffer.Length;
            }
        }

        public void AppendLine(string? line)
        {
            if (line == null)
                return;

            lock (_syncRoot)
            {
                _buffer.AppendLine(line);
                TrimIfNeeded();
            }
        }

        public override string ToString()
        {
            lock (_syncRoot)
                return _buffer.ToString();
        }

        private void TrimIfNeeded()
        {
            if (_buffer.Length <= _maxChars)
                return;

            int removeCount = _buffer.Length - _maxChars;
            int searchEnd = Math.Min(_buffer.Length, removeCount + 4096);
            for (int i = removeCount; i < searchEnd; i++)
            {
                if (_buffer[i] == '\n')
                {
                    removeCount = i + 1;
                    break;
                }
            }

            _buffer.Remove(0, Math.Min(removeCount, _buffer.Length));
        }
    }
}
