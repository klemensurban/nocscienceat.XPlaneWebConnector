// Copyright (c) 2026 Klemens Urban <klemens.urban@outlook.com>
// SPDX-License-Identifier: MIT

using nocscienceat.XPlaneWebConnector.Interfaces;

namespace nocscienceat.XPlaneWebConnector.VirtualDataRefs;

/// <summary>
/// Virtual dataref provider that verifies the ToLiss AirbusFBW plugin is alive
/// by performing a round-trip handshake via the LightDome dataref.
/// <para>
/// Write any <c>int</c> value to <c>xplanewebconnector/AirbusFBWalive</c> to
/// trigger the handshake. On success, emits <c>1</c>. On unexpected failure,
/// emits <c>0</c>. The handshake runs on a dedicated background task — it does
/// not block the Callback Task or any panel work queue.
/// </para>
/// <para>
/// A write while a handshake is already running is silently ignored.
/// </para>
/// </summary>
public class AirbusFbwAliveProvider : IVirtualDataRefProvider<int>
{
    private const string LightDomePath = "AirbusFBW/OHPLightSwitches[8]";

    public string Prefix => "AirbusFBWalive";

    private IXPlaneWebConnector? _connector;
    private Action<int>? _emit;
    private int _lightDomeValue = -1;
    private int _handshakeRunning;

    public async Task InitializeAsync(IXPlaneWebConnector connector, Action<int> emit)
    {
        _connector = connector;
        _emit = emit;

        await connector.SubscribeAsync(LightDomePath,
            (int value) => Volatile.Write(ref _lightDomeValue, value));
    }

    public void OnValueWritten(int value)
    {
        // Ignore if a handshake is already in progress.
        // OnValueWritten runs on the Callback Task (single-threaded),
        // but the background task also reads/writes _handshakeRunning,
        // so we use Interlocked for the flag.
        if (Interlocked.CompareExchange(ref _handshakeRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunHandshakeAsync();
            }
            catch (Exception)
            {
                _emit?.Invoke(0);
            }
            finally
            {
                Volatile.Write(ref _handshakeRunning, 0);
            }
        });
    }

    private async Task RunHandshakeAsync()
    {
        while (true)
        {
            // Set DIM (1)
            await _connector!.SetDataRefValueAsync(LightDomePath, 1);
            await Task.Delay(500);
            bool dimConfirmed = Volatile.Read(ref _lightDomeValue) == 1;

            await Task.Delay(500);

            // Set OFF (0)
            await _connector.SetDataRefValueAsync(LightDomePath, 0);
            await Task.Delay(500);
            bool offConfirmed = Volatile.Read(ref _lightDomeValue) == 0;

            await Task.Delay(500);

            if (dimConfirmed && offConfirmed)
            {
                _emit?.Invoke(1);
                return;
            }
        }
    }
}
