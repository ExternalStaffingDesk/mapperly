//HintName: Mapper.g.cs
#nullable enable
public partial class Mapper
{
    private partial TTarget Map<TSource, TTarget>(TSource source)
    {
        return source switch
        {
            global::A x when typeof(TTarget).IsAssignableFrom(typeof(global::B)) => (TTarget)(object)MapToB(x),
            global::C x when typeof(TTarget).IsAssignableFrom(typeof(global::D)) => (TTarget)(object)MapToD(x),
            null => throw new System.ArgumentNullException(nameof(source)),
            _ => throw new System.ArgumentException($"Cannot map {source.GetType()} to {typeof(TTarget)} as there is no known type mapping", nameof(source)),
        };
    }

    private partial global::B MapToB(global::A source)
    {
        var target = new global::B();
        target.Value = source.Value;
        return target;
    }

    private partial global::D MapToD(global::C source)
    {
        var target = new global::D(source.Value1);
        return target;
    }
}
