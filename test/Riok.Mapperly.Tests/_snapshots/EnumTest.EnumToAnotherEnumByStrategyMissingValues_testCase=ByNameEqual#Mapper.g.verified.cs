﻿//HintName: Mapper.g.cs
#nullable enable
public partial class Mapper
{
    private partial global::B Map(global::A source)
    {
        var target = new global::B();
        target.Value = MapToD(source.Value);
        return target;
    }

    private global::D MapToD(global::C source)
    {
        return source switch
        {
            global::C.Value1 => global::D.Value1,
            global::C.Value2 => global::D.Value2,
            _ => throw new System.ArgumentOutOfRangeException(nameof(source), source, "The value of enum C is not supported"),
        };
    }
}