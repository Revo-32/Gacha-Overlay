namespace GachaOverlay.Core.Discord.Messages;

public readonly record struct OptionalValue<T>
{
    private readonly T? _value;

    private OptionalValue(T? value)
    {
        HasValue = true;
        _value = value;
    }

    public bool HasValue { get; }

    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("The optional value is not present.");

    public static OptionalValue<T> From(T? value) => new(value);
}
