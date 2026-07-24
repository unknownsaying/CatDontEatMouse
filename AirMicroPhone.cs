/*
 * Air Micro Phone – C# (requires NAudio)
 * Install: dotnet add package NAudio
 * Records 10 seconds of audio and saves to "airmic.wav"
 */
using System;
using NAudio.Wave;

class AirMicroPhone
{
    static void Main(string[] args)
    {
        // Find the default microphone input
        var waveIn = new WaveInEvent();
        waveIn.WaveFormat = new WaveFormat(44100, 1); // 44.1 kHz, mono
        waveIn.DataAvailable += (s, e) => {
            // Data arrives as bytes – just accumulate for a simple WAV file
            // (In production you'd write directly to a file stream)
        };

        // Simple recorder that writes all data to a WAV file
        Console.WriteLine("Air Micro Phone - recording 10 seconds...");
        using (var writer = new WaveFileWriter("airmic.wav", waveIn.WaveFormat))
        {
            waveIn.DataAvailable += (s, a) => writer.Write(a.Buffer, 0, a.BytesRecorded);
            waveIn.StartRecording();
            System.Threading.Thread.Sleep(10000); // 10 seconds
            waveIn.StopRecording();
        }

        Console.WriteLine("Recording saved to airmic.wav");
    }
}