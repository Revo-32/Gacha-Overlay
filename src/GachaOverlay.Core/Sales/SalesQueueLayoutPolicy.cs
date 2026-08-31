namespace GachaOverlay.Core.Sales;

[Flags]
public enum SalesQueueVisibleFields
{
    None = 0,
    CurrentSeller = 1,
    WaitingCount = 2,
    Product = 4,
    NextWaitingUser = 8,
}

public sealed record SalesQueueLayoutInput(
    double AvailableWidth,
    double CurrentSellerWidth,
    double WaitingCountWidth,
    double ProductWidth,
    double NextWaitingUserWidth,
    SalesQueueVisibleFields RequestedFields,
    double FieldGap = 12d);

public sealed record SalesQueueLayoutResult(
    int LineCount,
    SalesQueueVisibleFields VisibleFields,
    SalesQueueVisibleFields FirstLineFields,
    SalesQueueVisibleFields SecondLineFields);

public static class SalesQueueLayoutPolicy
{
    public static SalesQueueLayoutResult Decide(SalesQueueLayoutInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var available = Math.Max(0d, input.AvailableWidth);
        var visible = input.RequestedFields;

        while (true)
        {
            if (Measure(visible, input) <= available)
            {
                return new SalesQueueLayoutResult(1, visible, visible, SalesQueueVisibleFields.None);
            }

            var first = visible &
                (SalesQueueVisibleFields.CurrentSeller | SalesQueueVisibleFields.Product);
            var second = visible &
                (SalesQueueVisibleFields.NextWaitingUser | SalesQueueVisibleFields.WaitingCount);
            if (second != SalesQueueVisibleFields.None &&
                Math.Max(Measure(first, input), Measure(second, input)) <= available)
            {
                return new SalesQueueLayoutResult(2, visible, first, second);
            }

            var next = DropLowestPriority(visible);
            if (next == visible || visible == SalesQueueVisibleFields.CurrentSeller)
            {
                return new SalesQueueLayoutResult(
                    1,
                    visible,
                    visible,
                    SalesQueueVisibleFields.None);
            }

            visible = next;
        }
    }

    private static SalesQueueVisibleFields DropLowestPriority(SalesQueueVisibleFields fields)
    {
        if (fields.HasFlag(SalesQueueVisibleFields.NextWaitingUser))
        {
            return fields & ~SalesQueueVisibleFields.NextWaitingUser;
        }

        if (fields.HasFlag(SalesQueueVisibleFields.Product))
        {
            return fields & ~SalesQueueVisibleFields.Product;
        }

        if (fields.HasFlag(SalesQueueVisibleFields.WaitingCount))
        {
            return fields & ~SalesQueueVisibleFields.WaitingCount;
        }

        return fields;
    }

    private static double Measure(
        SalesQueueVisibleFields fields,
        SalesQueueLayoutInput input)
    {
        var widths = new List<double>(4);
        Add(SalesQueueVisibleFields.CurrentSeller, input.CurrentSellerWidth);
        Add(SalesQueueVisibleFields.WaitingCount, input.WaitingCountWidth);
        Add(SalesQueueVisibleFields.Product, input.ProductWidth);
        Add(SalesQueueVisibleFields.NextWaitingUser, input.NextWaitingUserWidth);
        return widths.Sum() + Math.Max(0, widths.Count - 1) * input.FieldGap;

        void Add(SalesQueueVisibleFields field, double width)
        {
            if (fields.HasFlag(field))
            {
                widths.Add(Math.Max(0d, width));
            }
        }
    }
}
