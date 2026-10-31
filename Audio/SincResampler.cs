namespace PodBridge.Audio;

/// <summary>Kaiser pencereli polifaz sinc yeniden örnekleyici. Oran çalışırken değişebilir;
/// ses kartı saati ile ağ zaman çizelgesi arasındaki kaymayı bu sayede duyulmadan telafi ederiz.</summary>
public sealed class SincResampler
{
    private const int Taps = 32;
    private const int Phases = 512;
    private const double KaiserBeta = 9.0;

    private readonly float[] _table = new float[(Phases + 1) * Taps];
    private float[] _left = new float[8192];
    private float[] _right = new float[8192];
    private int _count;
    private double _position = Taps / 2 - 1;

    public SincResampler(double inputRate, double outputRate)
    {
        double cutoff = Math.Min(1.0, outputRate / inputRate) * 0.95;
        for (int phase = 0; phase <= Phases; phase++)
        {
            double frac = (double)phase / Phases;
            double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double x = tap - (Taps / 2 - 1) - frac;
                double sinc = Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                double t = x / (Taps / 2);
                double window = Math.Abs(t) >= 1 ? 0 : BesselI0(KaiserBeta * Math.Sqrt(1 - t * t)) / BesselI0(KaiserBeta);
                double value = sinc * window;
                _table[phase * Taps + tap] = (float)value;
                sum += value;
            }
            for (int tap = 0; tap < Taps; tap++) _table[phase * Taps + tap] /= (float)sum;
        }
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (int k = 1; k < 32; k++)
        {
            term *= x * x / (4.0 * k * k);
            sum += term;
            if (term < 1e-12 * sum) break;
        }
        return sum;
    }

    public void Push(float left, float right)
    {
        if (_count == _left.Length)
        {
            Array.Resize(ref _left, _count * 2);
            Array.Resize(ref _right, _count * 2);
        }
        _left[_count] = left;
        _right[_count] = right;
        _count++;
    }

    /// <summary>Girdi yettiği kadar çıktı üretir. step: girdi örneği / çıktı örneği.</summary>
    public int Pull(Span<float> interleavedOutput, double step)
    {
        int produced = 0;
        int maxFrames = interleavedOutput.Length / 2;
        while (produced < maxFrames)
        {
            int index = (int)_position;
            if (index + Taps / 2 >= _count) break;
            double scaled = (_position - index) * Phases;
            int phase = (int)scaled;
            float blend = (float)(scaled - phase);

            var a = _table.AsSpan(phase * Taps, Taps);
            var b = _table.AsSpan((phase + 1) * Taps, Taps);
            int first = index - (Taps / 2 - 1);
            float l = 0, r = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                float coefficient = a[tap] + (b[tap] - a[tap]) * blend;
                l += coefficient * _left[first + tap];
                r += coefficient * _right[first + tap];
            }
            interleavedOutput[produced * 2] = l;
            interleavedOutput[produced * 2 + 1] = r;
            produced++;
            _position += step;
        }

        int discard = (int)_position - (Taps / 2 - 1);
        if (discard > 0)
        {
            int keep = _count - discard;
            if (keep > 0)
            {
                Array.Copy(_left, discard, _left, 0, keep);
                Array.Copy(_right, discard, _right, 0, keep);
            }
            _count = Math.Max(keep, 0);
            _position -= discard;
        }
        return produced;
    }
}
