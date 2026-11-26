namespace SlimFaas.Kubernetes;

// Share the numerical rules across instant vectors, label groups and time windows.
internal struct PromQlAccumulator
{
    private int _count;
    private double _value;
    private double _correction;
    private bool _incrementalMean;

    public void Add(Aggregation operation, double sample)
    {
        _count++;
        if (_count == 1)
        {
            _value = sample;
            return;
        }

        switch (operation)
        {
            case Aggregation.Count: return;
            case Aggregation.Min: _value = double.MinNumber(_value, sample); return;
            case Aggregation.Max: _value = double.MaxNumber(_value, sample); return;
            case Aggregation.Average: AddToMean(sample); return;
            default: AddCompensated(sample); return;
        }
    }

    private void AddToMean(double sample)
    {
        // Stay in sum mode to retain small residuals. Switch to a weighted
        // mean if adding two finite values would overflow the partial sum.
        if (!_incrementalMean && double.IsFinite(_value) && double.IsFinite(sample) &&
            double.IsInfinity(_value + sample))
        {
            _value /= _count - 1;
            _correction /= _count - 1;
            _incrementalMean = true;
        }

        if (_incrementalMean)
        {
            double weight = (double)(_count - 1) / _count;
            _value *= weight;
            _correction *= weight;
            sample /= _count;
        }
        AddCompensated(sample);
    }

    private void AddCompensated(double sample)
    {
        double next = _value + sample;
        if (double.IsFinite(next))
        {
            // Neumaier compensation recovers the rounding error even when the
            // incoming term has a larger magnitude than the running sum.
            _correction += Math.Abs(_value) >= Math.Abs(sample)
                ? (_value - next) + sample
                : (sample - next) + _value;
        }
        else
        {
            _correction = 0;
        }
        _value = next;
    }

    public readonly double Result(Aggregation operation)
    {
        if (_count == 0) return double.NaN;
        return operation switch
        {
            Aggregation.Count => _count,
            Aggregation.Min or Aggregation.Max => _value,
            Aggregation.Average when !_incrementalMean => _value / _count + _correction / _count,
            _ => _value + _correction
        };
    }
}
