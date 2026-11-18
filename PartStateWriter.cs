using System;
using System.Reflection;
using TuneLab.Foundation;

namespace TuneLab.VstInstruments;

// Best-effort automatic write-back into the part's `state` property.
//
// SDK 1.2 exposes only read-only part properties to extensions, but the host
// data object behind them is writable. The current host exposes it as
// InstrumentSynthesisContext.Part (MidiPart) -> MidiPart.Properties
// (DataPropertyObject) -> SetValue(string, PropertyValue); the synthesis guard
// also keeps the same source in PropertyObjectGuard.mSource.
//
// This is deliberately reflection-based and optional: if a future host changes
// those names, TryWrite returns false and the UI falls back to the base64
// dialog (the user can still paste into the State field). No TuneLab source is
// modified.
internal static class PartStateWriter
{
    const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
    const BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static bool TryWrite(object? context, string key, string value)
    {
        if (context == null)
            return false;

        try
        {
            return TryWriteViaPart(context, key, value)
                || TryWriteViaGuard(context, key, value);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryRemove(object? context, string key)
    {
        if (context == null)
            return false;

        try
        {
            return TryRemoveViaPart(context, key)
                || TryRemoveViaGuard(context, key);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool TryRemoveViaPart(object context, string key)
    {
        object? part = context.GetType().GetProperty("Part", PublicInstance)?.GetValue(context);

        if (part == null)
            return false;

        object? properties = part.GetType().GetProperty("Properties", PublicInstance)?.GetValue(part);

        return TryRemoveValue(properties, key);
    }

    static bool TryRemoveViaGuard(object context, string key)
    {
        object? guard = context.GetType().GetProperty("PartProperties", PublicInstance)?.GetValue(context);

        if (guard == null)
            return false;

        object? source = guard.GetType().GetField("mSource", NonPublicInstance)?.GetValue(guard);

        return TryRemoveValue(source, key);
    }

    static bool TryRemoveValue(object? properties, string key)
    {
        if (properties == null)
            return false;

        var removeValue = properties.GetType().GetMethod("RemoveValue", PublicInstance,
            binder: null, types: [typeof(string)], modifiers: null);

        if (removeValue == null)
            return false;

        removeValue.Invoke(properties, [key]);
        return true;
    }

    static bool TryWriteViaPart(object context, string key, string value)
    {
        object? part = context.GetType().GetProperty("Part", PublicInstance)?.GetValue(context);

        if (part == null)
            return false;

        object? properties = part.GetType().GetProperty("Properties", PublicInstance)?.GetValue(part);

        return TrySetValue(properties, key, value);
    }

    static bool TryWriteViaGuard(object context, string key, string value)
    {
        object? guard = context.GetType().GetProperty("PartProperties", PublicInstance)?.GetValue(context);

        if (guard == null)
            return false;

        object? source = guard.GetType().GetField("mSource", NonPublicInstance)?.GetValue(guard);

        return TrySetValue(source, key, value);
    }

    static bool TrySetValue(object? properties, string key, string value)
    {
        if (properties == null)
            return false;

        var setValue = properties.GetType().GetMethod("SetValue", PublicInstance,
            binder: null, types: [typeof(string), typeof(PropertyValue)], modifiers: null);

        if (setValue == null)
            return false;

        setValue.Invoke(properties, [key, PropertyValue.Create(value)]);
        return true;
    }
}