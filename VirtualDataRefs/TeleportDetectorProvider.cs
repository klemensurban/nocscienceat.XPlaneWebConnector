// Copyright (c) 2026 Klemens Urban <klemens.urban@outlook.com>
// SPDX-License-Identifier: MIT

using nocscienceat.XPlaneWebConnector.Interfaces;

namespace nocscienceat.XPlaneWebConnector.VirtualDataRefs;

/// <summary>
/// Virtual dataref <c>xplanewebconnector/teleport</c>.
/// Subscribes to latitude/longitude and emits an <c>int</c> value via the
/// registry-provided <c>emit</c> delegate when the distance between consecutive
/// position updates exceeds a threshold:
/// <list type="bullet">
///   <item><c>1</c> — distance &gt; 1 000 m</item>
///   <item><c>2</c> — distance &gt; 2 000 m</item>
///   <item><c>3</c> — distance &gt; 50 km</item>
/// </list>
/// Uses squared thresholds to avoid <see cref="Math.Sqrt"/>.
/// Consumer management is handled by <see cref="VirtualEntry{T}"/>.
/// </summary>
internal sealed class TeleportDetectorProvider : IVirtualDataRefProvider<int>
{
    // Position state — accessed only on the callback drain task (single-threaded)
    private double _latitude, _longitude;
    private int _positionUpdates;   // counts first lat + first lon before distance checks begin
    private Action<int>? _emit;

    // Squared distance thresholds in meters²
    private const double Threshold1KmSq  =  1_000.0 *  1_000.0;
    private const double Threshold2KmSq  =  2_000.0 *  2_000.0;
    private const double Threshold50KmSq = 50_000.0 * 50_000.0;
    private const double MetersPerDegree = 111_320.0;

    public string Prefix => "teleport";

    public async Task InitializeAsync(IXPlaneWebConnector connector, Action<int> emit)
    {
        _emit = emit;

        // Subscribe to real X-Plane datarefs. Handles are intentionally NOT disposed —
        // the subscriptions stay alive for the lifetime of the connector.
        await connector.SubscribeAsync("sim/flightmodel/position/latitude", (double v) =>
        {
            CheckTeleport(v, _longitude);
            _latitude = v;
        });
        await connector.SubscribeAsync("sim/flightmodel/position/longitude", (double v) =>
        {
            CheckTeleport(_latitude, v);
            _longitude = v;
        });
    }

    private void CheckTeleport(double newLat, double newLon)
    {
        // Wait until both lat and lon have been received at 2 times 
        // to establish a valid baseline position.
        if (_positionUpdates < 4)
        {
            _positionUpdates++;
            return;
        }

        double dLatM = (newLat - _latitude) * MetersPerDegree;
        double dLonM = (newLon - _longitude) * MetersPerDegree * Math.Cos(_latitude * (Math.PI / 180.0));
        double distSq = dLatM * dLatM + dLonM * dLonM;

        int level;
        if (distSq > Threshold50KmSq) level = 3;
        else if (distSq > Threshold2KmSq) level = 2;
        else if (distSq > Threshold1KmSq) level = 1;
        else return;

        _emit!(level);
    }
}
