﻿using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using AssettoServer.Network.Packets.Outgoing;

namespace AssettoServer.Network.Packets.Incoming;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct PositionUpdateIn
{
    public readonly byte PakSequenceId;
    public readonly uint LastRemoteTimestamp;
    public readonly Vector3 Position;
    public readonly Vector3 Rotation;
    public readonly Vector3 Velocity;
    public readonly byte TyreAngularSpeedFL;
    public readonly byte TyreAngularSpeedFR;
    public readonly byte TyreAngularSpeedRL;
    public readonly byte TyreAngularSpeedRR;
    public readonly byte SteerAngle;
    public readonly byte WheelAngle;
    public readonly ushort EngineRpm;
    public readonly byte Gear;
    public readonly CarStatusFlags StatusFlag;
    public readonly short PerformanceDelta;
    public readonly byte Gas;
    public readonly float NormalizedPosition;

    [SuppressMessage("ReSharper", "EqualExpressionComparison")]
    public bool IsValid()
    {
        // Check if any vector component contains NaN. Equality check NaN == NaN returns false
        #pragma warning disable CS1718
        return Position == Position && Rotation == Rotation && Velocity == Velocity;
        #pragma warning restore CS1718
    }
}
