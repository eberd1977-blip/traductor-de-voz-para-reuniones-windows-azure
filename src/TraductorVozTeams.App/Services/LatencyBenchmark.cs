using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TraductorVozTeams.App.Audio;
using TraductorVozTeams.Core.Audio;
using TraductorVozTeams.Core.Devices;
using TraductorVozTeams.Core.Metrics;
using TraductorVozTeams.Core.Providers;
using TraductorVozTeams.Core.Settings;

namespace TraductorVozTeams.App.Services;

/// <summary>
/// Prueba de demora: <c>TraductorVozTeams.exe --latency-test [informe.txt] [--azure] [--repeticiones N]
/// [--voz-es VOZ] [--salida TEXTO] [--precio-hora USD]</c>.
/// 1) Sin credenciales: mide el tramo local app → Virtual Audio Cable → extremo de grabación (lo que recibe Teams).
/// 2) Con <c>--azure</c> y el perfil activo: genera frases en español con voz sintética, las envía en tiempo real
///    al mismo adaptador que usa la app y mide fin de la voz → primer audio en inglés, calidad orientativa y uso facturable.
/// </summary>
public static class LatencyBenchmark
{
    /// <summary>Precio de lista de referencia (USD por hora de audio, S1 Speech Translation), consultado el 2026-11-17.</summary>
    public const double ReferencePricePerAudioHourUsd = 2.5;

    private const int ChunkMs = 20;

    public sealed record Options(
        string? ReportPath,
        bool UseAzure,
        int Repetitions,
        string? SpanishVoice,
        string? OutputFilter,
        double PricePerAudioHourUsd,
        int LoopbackTrials,
        bool Incoming = false);

    private sealed record Phrase(string Source, string Reference);

    // Dirección entrante: lo que dirían los demás participantes en inglés, con una referencia en español.
    private static readonly Phrase[] IncomingPhrases =
    [
        new("Good morning everyone, can you hear me well?", "Buenos días a todos, ¿me escuchan bien?"),
        new("Let's start with a quick update on the project.", "Empecemos con una breve actualización del proyecto."),
        new("We finished the first phase last week.", "Terminamos la primera fase la semana pasada."),
        new("The client asked for a new version of the report.", "El cliente pidió una nueva versión del informe."),
        new("Could you share the budget numbers by Thursday?", "¿Podrías compartir las cifras del presupuesto antes del jueves?"),
        new("Thank you, that is all for today.", "Gracias, eso es todo por hoy."),
    ];

    private static readonly Phrase[] Phrases =
    [
        new("Buenos días a todos, gracias por venir a la reunión.", "Good morning everyone, thank you for coming to the meeting."),
        new("Hoy vamos a revisar el avance del proyecto y los próximos pasos.", "Today we are going to review the progress of the project and the next steps."),
        new("¿Pueden ver mi pantalla correctamente?", "Can you see my screen correctly?"),
        new("El informe estará listo el viernes por la tarde.", "The report will be ready on Friday afternoon."),
        new("Necesitamos aprobar el presupuesto antes de fin de mes.", "We need to approve the budget before the end of the month."),
        new("Si tienen alguna pregunta, por favor díganme.", "If you have any questions, please let me know."),
    ];

    public static Options Parse(string[] args, int startIndex)
    {
        string? report = null;
        var azure = false;
        var repetitions = 1;
        string? voice = null;
        string? output = null;
        var price = ReferencePricePerAudioHourUsd;
        var incoming = false;
        for (var i = startIndex + 1; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {a}.");
            switch (a.ToLowerInvariant())
            {
                case "--azure": azure = true; break;
                case "--repeticiones": repetitions = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 10); break;
                case "--voz-es": voice = Next(); break;
                case "--voz-origen": voice = Next(); break;
                case "--direccion":
                    incoming = Next().ToLowerInvariant() switch
                    {
                        "entrante" => true,
                        "saliente" => false,
                        _ => throw new ArgumentException("--direccion admite \"saliente\" o \"entrante\"."),
                    };
                    break;
                case "--salida": output = Next(); break;
                case "--precio-hora": price = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Opción desconocida: {a}");
                    }

                    report = a;
                    break;
            }
        }

        return new Options(report, azure, repetitions, voice, output, price, 20, incoming);
    }

    public static int Run(Options options) => Task.Run(() => RunCore(options)).GetAwaiter().GetResult();

    private static int RunCore(Options options)
    {
        var paths = AppPaths.Default();
        var reportPath = options.ReportPath
            ?? Path.Combine(paths.MetricsDirectory, $"prueba-demora-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var report = new StringBuilder();
        var ok = true;

        report.AppendLine("Prueba de demora - Traductor de voz para reuniones en Windows más Azure");
        report.AppendLine($"Fecha local: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"Ejecutable: {Environment.ProcessPath} ({typeof(LatencyBenchmark).Assembly.GetName().Version})");
        report.AppendLine($"SO: {Environment.OSVersion.VersionString}; equipo: {Environment.MachineName}");
        report.AppendLine("Los valores son mediciones de esta ejecución, no garantías. Dependen de la red, la región, el equipo y la carga del servicio.");
        report.AppendLine();

        double? loopbackMedian = null;
        try
        {
            loopbackMedian = RunLoopback(options, paths, report);
        }
        catch (Exception ex)
        {
            ok = false;
            report.AppendLine($"[FALLO] Tramo local: {ex.GetType().Name}: {ex.Message}");
        }

        report.AppendLine();
        if (options.UseAzure)
        {
            try
            {
                ok &= RunAzureAsync(options, paths, loopbackMedian, report).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                ok = false;
                report.AppendLine($"[FALLO] Prueba con Azure: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            report.AppendLine("== 2. Azure AI Speech: OMITIDA ==");
            report.AppendLine("Añade --azure para medir con tu perfil activo (consume unos segundos de audio facturable).");
        }

        report.AppendLine();
        report.AppendLine(ok ? "RESULTADO: OK" : "RESULTADO: FALLO");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
        return ok ? 0 : 1;
    }

    private static double? RunLoopback(Options options, AppPaths paths, StringBuilder report)
    {
        report.AppendLine("== 1. Tramo local: app → Virtual Audio Cable → extremo de grabación (lo que recibe Teams) ==");
        var settings = new SettingsStore(paths.SettingsFile).Load();
        var patterns = settings.VirtualCablePatterns;
        var renders = AudioDeviceService.List(AudioDeviceFlow.Render);
        var captures = AudioDeviceService.List(AudioDeviceFlow.Capture);

        AudioDeviceInfo? render = null;
        if (options.OutputFilter is { } filter)
        {
            render = renders.FirstOrDefault(d => d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        render ??= renders.FirstOrDefault(d => d.Id == settings.TeamsOutputDeviceId);
        render ??= renders.FirstOrDefault(d => DeviceRoutingValidator.IsVirtualCable(d, patterns));
        if (render is null || !DeviceRoutingValidator.IsVirtualCable(render, patterns))
        {
            report.AppendLine("[OMITIDA] No se encontró una salida de cable virtual.");
            return null;
        }

        var capture = FindPairedCapture(render, captures, patterns);
        if (capture is null)
        {
            report.AppendLine($"[OMITIDA] No se encontró el extremo de grabación que corresponde a \"{render.Name}\".");
            return null;
        }

        report.AppendLine($"Salida usada por la app: {render.Name}");
        report.AppendLine($"Extremo de grabación (como micrófono de Teams): {capture.Name}");
        report.AppendLine("Aviso: durante la prueba se envían tonos breves al cable. No la ejecutes con una llamada de Teams en curso.");

        var stopwatch = Stopwatch.StartNew();
        var onsets = new ConcurrentQueue<double>();
        var armed = 0;
        var lastPeak = 0f;
        using var captureDevice = AudioDeviceService.Open(capture.Id);
        var busy = OtherActiveCaptureProcesses(captureDevice);
        if (busy.Count > 0)
        {
            report.AppendLine($"[OMITIDA] Otro proceso está capturando del cable ahora mismo ({string.Join(", ", busy)}); los tonos llegarían a una llamada. Repite la prueba sin una llamada activa.");
            return null;
        }

        using var wasapiCapture = new WasapiCapture(captureDevice, true, 10);
        var format = SampleProviders.Normalize(wasapiCapture.WaveFormat);
        report.AppendLine($"Formato de captura del cable: {format}");

        // Detección por energía en una ventana de 5 ms: el tono (pico 0,5) tiene RMS ≈ 0,35;
        // los chasquidos breves observados en el cable alcanzaban picos de 0,1-0,3 pero poca energía.
        const double RmsThreshold = 0.2;
        var window = Math.Max(1, format.SampleRate / 200);
        var squares = new double[window];
        var position = 0;
        var sum = 0.0;
        wasapiCapture.DataAvailable += (_, e) =>
        {
            var now = stopwatch.Elapsed.TotalMilliseconds;
            var isArmed = Volatile.Read(ref armed) == 1;
            var frames = e.BytesRecorded / format.BlockAlign;
            for (var i = 0; i < frames; i++)
            {
                var sample = ReadSample(e.Buffer, i * format.BlockAlign, format);
                var square = (double)sample * sample;
                sum += square - squares[position];
                squares[position] = square;
                position = (position + 1) % window;
                if (isArmed && sum / window >= RmsThreshold * RmsThreshold)
                {
                    isArmed = false;
                    Interlocked.Exchange(ref armed, 0);
                    Volatile.Write(ref lastPeak, (float)Math.Sqrt(Math.Max(0, sum) / window));

                    // El umbral se alcanza cuando el tono ocupa ~1/3 de la ventana; se corrige ese desfase.
                    onsets.Enqueue(now - ((frames - i + (window / 3.0)) * 1000.0 / format.SampleRate));
                }
            }
        };

        string ListenWithoutSending(string label, int milliseconds)
        {
            var found = new List<string>();
            var start = stopwatch.Elapsed.TotalMilliseconds;
            while (onsets.TryDequeue(out _))
            {
            }

            Volatile.Write(ref armed, 1);
            while (stopwatch.Elapsed.TotalMilliseconds < start + milliseconds)
            {
                if (onsets.TryDequeue(out var at))
                {
                    found.Add($"{at - start:0} ms (RMS {Volatile.Read(ref lastPeak):0.00})");
                    Thread.Sleep(100);
                    Volatile.Write(ref armed, 1);
                }

                Thread.Sleep(5);
            }

            Volatile.Write(ref armed, 0);
            return $"Sonidos en el cable sin enviar tonos, {label} ({milliseconds / 1000} s): {found.Count}" +
                (found.Count > 0 ? " → " + string.Join("; ", found.Take(10)) : string.Empty);
        }

        var results = new List<double>();
        var lost = 0;
        var discarded = 0;
        wasapiCapture.StartRecording();
        try
        {
            Thread.Sleep(300);
            report.AppendLine(ListenWithoutSending("antes de abrir la salida de la app", 3000));
            using var output = new AudioOutput(AudioDeviceService.Open(render.Id), PcmFormat.SpeechInput, 1f);
            Thread.Sleep(800);

            // Otro audio en el cable falsearía la medición; se informa para poder interpretarla.
            report.AppendLine(ListenWithoutSending("con la salida de la app abierta", 3000));

            var tone = Tone(PcmFormat.SpeechInput, 1000, 60, 0.5);
            for (var trial = 0; trial < options.LoopbackTrials; trial++)
            {
                while (onsets.TryDequeue(out _))
                {
                }

                Volatile.Write(ref armed, 1);
                var sent = stopwatch.Elapsed.TotalMilliseconds;
                output.Enqueue(new AudioChunk(tone, PcmFormat.SpeechInput));
                var received = false;
                while (stopwatch.Elapsed.TotalMilliseconds < sent + 2000)
                {
                    if (onsets.TryDequeue(out var onset))
                    {
                        if (onset - sent < 5)
                        {
                            // Llegó antes de lo físicamente posible: otro sonido en el cable. Se descarta y se sigue esperando.
                            discarded++;
                            Volatile.Write(ref armed, 1);
                            continue;
                        }

                        results.Add(onset - sent);
                        received = true;
                        break;
                    }

                    Thread.Sleep(2);
                }

                if (!received)
                {
                    Volatile.Write(ref armed, 0);
                    lost++;
                }

                Thread.Sleep(400);
            }
        }
        finally
        {
            wasapiCapture.StopRecording();
        }

        var summary = MeasurementStats.Summarize(results);
        report.AppendLine($"Tonos enviados: {options.LoopbackTrials}; no recibidos: {lost}; detecciones anticipadas descartadas: {discarded}");
        report.AppendLine("Demora entrega a la salida → llegada al extremo de grabación: " + summary);
        report.AppendLine("Valores individuales (ms): " + string.Join(", ", results.Select(v => v.ToString("0", CultureInfo.InvariantCulture))));
        report.AppendLine("Incluye el búfer de salida de la app (WASAPI compartido, 100 ms solicitados), el cable y un búfer de captura de 10 ms.");
        report.AppendLine("Precisión aproximada ±10-20 ms. Teams usa su propio búfer de captura y procesamiento, que no se mide aquí.");
        return summary.Count > 0 ? summary.Median : null;
    }

    private static async Task<bool> RunAzureAsync(Options options, AppPaths paths, double? loopbackMedian, StringBuilder report)
    {
        var phrases = options.Incoming ? IncomingPhrases : Phrases;
        var (sourceName, targetName, sourceCode, targetCode) = options.Incoming
            ? ("inglés", "español", "en", "ES")
            : ("español", "inglés", "es", "EN");
        report.AppendLine($"== 2. Azure AI Speech ({(options.Incoming ? "dirección entrante" : "dirección saliente")}): fin de la voz en {sourceName} → primer audio en {targetName} ==");
        var services = new AppServices(paths);
        var profile = services.Settings.ActiveProfile;
        if (profile?.ProviderType == RealtimeTranslateDescriptor.Type)
        {
            report.AppendLine("[FALLO] Esta prueba mide frases de Azure AI Speech. Para GPT usa la verificación guiada y el protocolo comparativo del README.");
            return false;
        }
        if (profile is null || !services.Factories.TryGetValue(profile.ProviderType, out var factory))
        {
            report.AppendLine("[FALLO] No hay un perfil activo de un proveedor disponible. Configúralo en la app (Proveedores).");
            return false;
        }

        var context = options.Incoming ? services.CreateIncomingContext(profile) : services.CreateContext(profile);
        if (!context.Secrets.ContainsKey(AzureSpeechDescriptor.ApiKeyKey))
        {
            report.AppendLine($"[FALLO] El perfil \"{profile.Name}\" no tiene clave guardada.");
            return false;
        }

        var values = context.Values;
        var mode = ProviderValues.Get(values, AzureSpeechDescriptor.ConnectionModeKey);
        var location = mode == AzureSpeechDescriptor.ModeEndpoint
            ? "endpoint " + ProviderValues.Get(values, AzureSpeechDescriptor.EndpointKey)
            : "región " + ProviderValues.Get(values, AzureSpeechDescriptor.RegionKey);
        var spanishVoice = options.SpanishVoice ?? (options.Incoming ? DefaultEnglishVoice(context.SourceLanguage) : DefaultSpanishVoice(context.SourceLanguage));
        report.AppendLine($"Perfil: {profile.Name}; {location}; idioma origen {context.SourceLanguage}; voz {targetCode} {ProviderValues.Get(values, AzureSpeechDescriptor.VoiceNameKey)}");
        report.AppendLine($"Silencio de segmentación: {(context.SegmentationSilenceMs > 0 ? context.SegmentationSilenceMs + " ms" : "predeterminado del servicio")}");
        report.AppendLine($"Audio de prueba: {phrases.Length} frases × {options.Repetitions} repetición(es), sintetizadas con {spanishVoice} y enviadas en tiempo real.");

        var fixtures = new List<short[]>();
        var ttsCharacters = 0;
        using (var synthesizer = CreateSynthesizer(context, spanishVoice))
        {
            foreach (var phrase in phrases)
            {
                using var result = await synthesizer.SpeakTextAsync(phrase.Source).ConfigureAwait(false);
                if (result.Reason != ResultReason.SynthesizingAudioCompleted)
                {
                    var details = SpeechSynthesisCancellationDetails.FromResult(result);
                    report.AppendLine($"[FALLO] Síntesis de la frase de prueba: {details.ErrorCode} - {details.ErrorDetails}");
                    return false;
                }

                ttsCharacters += phrase.Source.Length;
                fixtures.Add(ToSamples(result.AudioData));
            }
        }

        // Flujo: 1 s de silencio, frases separadas por 2 s de silencio y 3 s finales.
        var rate = PcmFormat.SpeechInput.SampleRate;
        var stream = new List<short>();
        var segments = new List<(int Phrase, int StartSample, int VoiceEndSample)>();
        stream.AddRange(new short[rate]);
        for (var rep = 0; rep < options.Repetitions; rep++)
        {
            for (var p = 0; p < fixtures.Count; p++)
            {
                var start = stream.Count;
                var lastVoiced = Array.FindLastIndex(fixtures[p], s => Math.Abs((int)s) >= 500);
                segments.Add((p, start, start + Math.Max(lastVoiced, 0)));
                stream.AddRange(fixtures[p]);
                stream.AddRange(new short[rate * 2]);
            }
        }

        stream.AddRange(new short[rate * 3]);
        var samples = stream.ToArray();

        var stopwatch = Stopwatch.StartNew();
        var gate = new object();
        var recognized = new List<Recognition>();
        var pending = new Queue<Recognition>();
        var audioInProgress = false;
        var receivedAudioBytes = 0L;
        var errors = new ConcurrentQueue<string>();
        var tracker = new LatencyTracker();

        await using var provider = factory.Create(context);
        provider.Recognized += (_, e) =>
        {
            var now = stopwatch.Elapsed.TotalMilliseconds;
            lock (gate)
            {
                var rec = new Recognition(e.AudioOffset?.TotalMilliseconds, e.AudioDuration?.TotalMilliseconds, now, e.SourceText, e.TranslatedText);
                recognized.Add(rec);
                pending.Enqueue(rec);
                tracker.OnRecognized(DateTimeOffset.UtcNow, e.AudioOffset, e.AudioDuration, e.SourceText, e.TranslatedText);
            }
        };
        provider.AudioReceived += (_, e) =>
        {
            var now = stopwatch.Elapsed.TotalMilliseconds;
            lock (gate)
            {
                if (e.Chunk.IsEmpty)
                {
                    audioInProgress = false;
                    tracker.OnAudio(DateTimeOffset.UtcNow, true);
                    return;
                }

                receivedAudioBytes += e.Chunk.Data.Length;
                if (!audioInProgress && pending.Count > 0)
                {
                    audioInProgress = true;
                    pending.Dequeue().FirstAudioMs = now;
                }

                tracker.OnAudio(DateTimeOffset.UtcNow, false);
            }
        };
        provider.Error += (_, e) => errors.Enqueue(e.Message);

        await provider.StartAsync(CancellationToken.None).ConfigureAwait(false);

        var chunkSamples = rate * ChunkMs / 1000;
        var buffer = new byte[chunkSamples * 2];
        var pushTimes = new double[(samples.Length + chunkSamples - 1) / chunkSamples];
        var expected = segments.Count;
        var streamStart = stopwatch.Elapsed.TotalMilliseconds;
        tracker.MarkStreamStarted(DateTimeOffset.UtcNow);
        var sentSamples = 0L;
        for (var k = 0; ; k++)
        {
            var finishedStream = k >= pushTimes.Length;
            if (finishedStream)
            {
                bool done;
                lock (gate)
                {
                    done = recognized.Count(r => r.FirstAudioMs is not null) >= expected;
                }

                if (done || !errors.IsEmpty || stopwatch.Elapsed.TotalMilliseconds > streamStart + (pushTimes.Length * ChunkMs) + 15000)
                {
                    break;
                }
            }

            var due = streamStart + ((k + 1) * ChunkMs);
            while (stopwatch.Elapsed.TotalMilliseconds < due)
            {
                Thread.Sleep(1);
            }

            Array.Clear(buffer);
            if (!finishedStream)
            {
                var offset = k * chunkSamples;
                var count = Math.Min(chunkSamples, samples.Length - offset);
                Buffer.BlockCopy(samples, offset * 2, buffer, 0, count * 2);
                pushTimes[k] = stopwatch.Elapsed.TotalMilliseconds;
            }

            provider.PushAudio(buffer, buffer.Length);
            sentSamples += chunkSamples;
        }

        await provider.StopAsync().ConfigureAwait(false);

        foreach (var error in errors.Distinct())
        {
            report.AppendLine("[ERROR DEL SERVICIO] " + error);
        }

        List<Recognition> results;
        lock (gate)
        {
            results = [.. recognized];
        }

        var totals = new List<double>();
        var segmentsWithAudio = new HashSet<int>();
        var recognition = new List<double>();
        var spanishWer = new List<double>();
        var englishWer = new List<double>();
        report.AppendLine();
        report.AppendLine($"# | fin voz→texto (ms) | fin voz→audio {targetCode} (ms) | WER {sourceCode} | WER {targetCode.ToLowerInvariant()}* | reconocido / traducido / referencia");
        var index = 0;
        foreach (var rec in results)
        {
            index++;
            var mid = (rec.OffsetMs ?? 0) + ((rec.DurationMs ?? 0) / 2);
            var segment = segments
                .Select((s, i) => (s, i))
                .FirstOrDefault(x => mid * rate / 1000 >= x.s.StartSample && mid * rate / 1000 <= x.s.VoiceEndSample + (rate / 2));
            if (rec.OffsetMs is null || segment == default)
            {
                report.AppendLine($"{index} | sin correspondencia con una frase | | | | {rec.Spanish} / {rec.English}");
                continue;
            }

            var phrase = phrases[segment.s.Phrase];
            var voiceEndWall = pushTimes[segment.s.VoiceEndSample / chunkSamples];
            var toText = rec.RecognizedMs - voiceEndWall;
            recognition.Add(toText);
            double? total = rec.FirstAudioMs is { } audio ? audio - voiceEndWall : null;
            if (total is { } t)
            {
                totals.Add(t);
                segmentsWithAudio.Add(segment.i);
            }

            var werEs = WordErrorRate.Compute(phrase.Source, rec.Spanish);
            var werEn = WordErrorRate.Compute(phrase.Reference, rec.English);
            spanishWer.Add(werEs);
            englishWer.Add(werEn);
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{index} | {toText:0} | {(total is { } tt ? tt.ToString("0", CultureInfo.InvariantCulture) : "sin audio")} | {werEs:P0} | {werEn:P0} | {rec.Spanish} / {rec.English} / {phrase.Reference}"));
        }

        var totalSummary = MeasurementStats.Summarize(totals);
        var appMetric = MeasurementStats.Summarize(tracker.Completed.Select(u => u.EndToEnd).OfType<TimeSpan>().Select(t => t.TotalMilliseconds));
        report.AppendLine();
        report.AppendLine($"Frases enviadas: {expected}; resultados reconocidos: {results.Count}; con audio en {targetName}: {totals.Count}");
        report.AppendLine("Fin de la voz → texto traducido: " + MeasurementStats.Summarize(recognition));
        report.AppendLine($"Fin de la voz → primer audio {targetCode} recibido (medido con el fin real del audio enviado): " + totalSummary);
        report.AppendLine("Misma demora según la métrica que muestra la app (offset/duración del servicio): " + appMetric);
        if (segmentsWithAudio.Count == expected && totalSummary.Count == expected && loopbackMedian is { } loop)
        {
            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Estimación hasta la entrada de Teams (mediana servicio + mediana tramo local): {totalSummary.Median + loop:0} ms. Sin contar el procesamiento ni la red de Teams."));
        }

        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Calidad: WER medio del reconocimiento en {sourceName} {Average(spanishWer):P0}; WER medio {targetCode} frente a la referencia {Average(englishWer):P0}."));
        report.AppendLine($"* El WER {targetCode} es solo orientativo: una traducción correcta puede usar otras palabras. Revisa las frases a mano.");

        var audioSeconds = sentSamples / (double)rate;
        report.AppendLine();
        report.AppendLine("== 3. Uso facturable de esta prueba ==");
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Audio enviado a la traducción de voz: {audioSeconds:0.0} s (la traducción de voz se factura por segundo de audio enviado)."));
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Audio en {targetName} recibido: {receivedAudioBytes / (double)PcmFormat.SpeechInput.AverageBytesPerSecond:0.0} s aprox. Caracteres de síntesis para crear el audio de prueba: {ttsCharacters}."));
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Costo orientativo de la traducción con nivel Standard (S0; medidor de precios «S1 Speech Translation»): {audioSeconds / 3600 * options.PricePerAudioHourUsd:0.0000} USD, con un precio de lista de {options.PricePerAudioHourUsd} USD por hora de audio (--precio-hora)."));
        report.AppendLine("Ese precio es una referencia de la API pública de precios de Azure, consultada el 2026-11-17, sin impuestos, descuentos ni conversión de moneda.");
        report.AppendLine("Con el nivel F0 las primeras 5 horas al mes no tienen costo. Confirma el importe real en Azure Cost Management de tu suscripción.");

        if (segmentsWithAudio.Count != expected || totals.Count != expected)
        {
            report.AppendLine($"[FALLO] Audio asociado a {segmentsWithAudio.Count} de {expected} frases; la medicion esta incompleta.");
            return false;
        }

        return errors.IsEmpty;
    }

    private sealed class Recognition(double? offsetMs, double? durationMs, double recognizedMs, string spanish, string english)
    {
        public double? OffsetMs { get; } = offsetMs;

        public double? DurationMs { get; } = durationMs;

        public double RecognizedMs { get; } = recognizedMs;

        public string Spanish { get; } = spanish;

        public string English { get; } = english;

        public double? FirstAudioMs { get; set; }
    }

    private static double Average(List<double> values) => values.Count == 0 ? double.NaN : values.Average();

    /// <summary>Procesos (distintos de este) con una sesión de captura activa en el dispositivo, p. ej. Teams en una llamada.</summary>
    private static List<string> OtherActiveCaptureProcesses(MMDevice device)
    {
        var result = new List<string>();
        var sessions = device.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive
                || session.GetProcessID == Environment.ProcessId)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById((int)session.GetProcessID);
                result.Add(process.ProcessName);
            }
            catch (ArgumentException)
            {
                result.Add("PID " + session.GetProcessID);
            }
        }

        return result;
    }

    private static SpeechSynthesizer CreateSynthesizer(ProviderSessionContext context, string voice)
    {
        var values = context.Values;
        var key = context.Secrets[AzureSpeechDescriptor.ApiKeyKey];
        var config = ProviderValues.Get(values, AzureSpeechDescriptor.ConnectionModeKey) == AzureSpeechDescriptor.ModeEndpoint
            ? SpeechConfig.FromEndpoint(new Uri(ProviderValues.Get(values, AzureSpeechDescriptor.EndpointKey)), key)
            : SpeechConfig.FromSubscription(key, ProviderValues.Get(values, AzureSpeechDescriptor.RegionKey));
        config.SpeechSynthesisVoiceName = voice;
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);
        return new SpeechSynthesizer(config, (AudioConfig?)null);
    }

    private static string DefaultEnglishVoice(string sourceLanguage) => sourceLanguage.ToLowerInvariant() switch
    {
        "en-gb" => "en-GB-SoniaNeural",
        "en-au" => "en-AU-NatashaNeural",
        "en-ca" => "en-CA-ClaraNeural",
        "en-in" => "en-IN-NeerjaNeural",
        _ => "en-US-JennyNeural",
    };

    private static string DefaultSpanishVoice(string sourceLanguage) => sourceLanguage.ToLowerInvariant() switch
    {
        "es-ar" => "es-AR-ElenaNeural",
        "es-mx" => "es-MX-DaliaNeural",
        "es-co" => "es-CO-SalomeNeural",
        "es-us" => "es-US-PalomaNeural",
        _ => "es-ES-ElviraNeural",
    };

    private static short[] ToSamples(byte[] pcm)
    {
        var result = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, result, 0, result.Length * 2);
        return result;
    }

    private static byte[] Tone(PcmFormat format, int frequency, int durationMs, double amplitude)
    {
        var count = format.SampleRate * durationMs / 1000;
        var data = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(data.AsSpan(i * 2), value);
        }

        return data;
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format) => format.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat => BitConverter.ToSingle(buffer, offset),
        _ => format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            24 => ((buffer[offset] << 8) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 24)) / 2147483648f,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            _ => 0f,
        },
    };

    /// <summary>
    /// Busca el extremo de grabación del mismo cable: igual nombre (Virtual Audio Cable: "Line 1 (Virtual Audio Cable)")
    /// o mismo sufijo entre paréntesis con "Input"→"Output" (VB-Audio).
    /// </summary>
    internal static AudioDeviceInfo? FindPairedCapture(AudioDeviceInfo render, IReadOnlyList<AudioDeviceInfo> captures, IEnumerable<string> patterns)
    {
        var exact = captures.FirstOrDefault(c => string.Equals(c.Name, render.Name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var swapped = render.Name.Replace("Input", "Output", StringComparison.OrdinalIgnoreCase);
        var vb = captures.FirstOrDefault(c => string.Equals(c.Name, swapped, StringComparison.OrdinalIgnoreCase));
        if (vb is not null)
        {
            return vb;
        }

        var paren = render.Name.IndexOf('(', StringComparison.Ordinal);
        var prefix = (paren > 0 ? render.Name[..paren] : render.Name).Trim();
        return captures.FirstOrDefault(c => DeviceRoutingValidator.IsVirtualCable(c, patterns)
            && c.Name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase));
    }
}
