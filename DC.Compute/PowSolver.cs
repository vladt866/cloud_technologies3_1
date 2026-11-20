/*using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DC.Compute;

public sealed class PowSolver : IPowSolver
{
    public const int TaskCount = 100;
    public const int MaximumDifficultyBits = 24;
    public const int MaximumWorkers = TaskCount;

    public Task<PowResult> SolveAsync(string seed, int difficultyBits, int workerCount,
        IProgress<PowProgress>? progress, CancellationToken cancellationToken, PowTelemetry? telemetry = null)
    {
        if (seed is null || seed.Length != 6 || seed.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("The seed must contain exactly six decimal digits (0–9).", nameof(seed));
        if (difficultyBits is < 1 or > MaximumDifficultyBits)
            throw new ArgumentOutOfRangeException(nameof(difficultyBits), "Difficulty must be between 1 and 24 bits.");
        if (workerCount is < 1 or > MaximumWorkers)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be between 1 and 100.");

        return Task.Run(() =>
        {
            var solutions = new PowSolution?[TaskCount];
            int completed = 0;
            long totalAttempts = 0;

            // Chunk size ≈ 1/32 от ожидаемого числа хешей на одну задачу.
            // Диапазон = chunkSize × 4096 → при diff=24 это ≈128× среднего,
            // т.е. вероятность не найти решение астрономически мала.
            long chunkSize = Math.Clamp((1L << difficultyBits) / 32, 1L << 12, 1L << 22);
            long rangeEnd = chunkSize * 4096;

            var queue = new ConcurrentQueue<Chunk>();
            for (int taskId = 0; taskId < TaskCount; taskId++)
                for (long from = 0; from < rangeEnd; from += chunkSize)
                    queue.Enqueue(new Chunk(taskId, (ulong)from,
                                            (ulong)Math.Min(from + chunkSize, rangeEnd)));

            var elapsed = Stopwatch.StartNew();
            telemetry?.Start();
            try
            {
                var workers = new Task[workerCount];
                for (int w = 0; w < workerCount; w++)
                {
                    int lane = w;
                    workers[w] = Task.Run(() =>
                    {
                        while (queue.TryDequeue(out Chunk chunk))
                        {
                            if (cancellationToken.IsCancellationRequested) return;
                            if (Volatile.Read(ref solutions[chunk.TaskId]) != null) continue;
                            SolveChunk(lane, chunk, seed, difficultyBits, solutions,
                                ref completed, ref totalAttempts, progress, telemetry, cancellationToken);
                        }
                    }, cancellationToken);
                }

                Task.WaitAll(workers);
                elapsed.Stop();
                cancellationToken.ThrowIfCancellationRequested();

                var finalSolutions = new PowSolution[TaskCount];
                for (int i = 0; i < TaskCount; i++)
                {
                    if (solutions[i] is null)
                        throw new InvalidOperationException($"Task {i} could not be solved within its nonce range.");
                    finalSolutions[i] = solutions[i]!;
                }

                return new PowResult(Array.AsReadOnly(finalSolutions), elapsed.Elapsed,
                    completed, workerCount, totalAttempts);
            }
            finally { telemetry?.Finish(); }
        }, cancellationToken);
    }

    private static void SolveChunk(int lane, in Chunk chunk, string seed, int difficultyBits,
        PowSolution?[] solutions, ref int completed, ref long totalAttempts,
        IProgress<PowProgress>? progress, PowTelemetry? telemetry, CancellationToken cancellationToken)
    {
        int taskLane = telemetry?.BeginWork(chunk.TaskId, lane) ?? -1;
        byte[] prefix = Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"BSU-POW-1|{seed}|{chunk.TaskId}|"));
        Span<byte> message = stackalloc byte[64];
        Span<byte> hash = stackalloc byte[32];
        prefix.AsSpan().CopyTo(message);

        long attempts = 0;
        long reported = 0;

        try
        {
            for (ulong nonce = chunk.From; nonce < chunk.To; nonce++)
            {
                if ((nonce & 255UL) == 0)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    // Периодически проверяем, не решил ли эту же задачу другой воркер.
                    if ((nonce & 8191UL) == 0 && Volatile.Read(ref solutions[chunk.TaskId]) != null) return;
                }

                if (!Utf8Formatter.TryFormat(nonce, message[prefix.Length..], out int digits))
                    throw new InvalidOperationException("Could not encode the nonce.");

                SHA256.HashData(message[..(prefix.Length + digits)], hash);
                attempts = checked(attempts + 1);

                if (telemetry is not null && (attempts & 8191) == 0)
                {
                    telemetry.AddAttempts(taskLane, chunk.TaskId, attempts - reported);
                    reported = attempts;
                }

                if (HasLeadingZeroBits(hash, difficultyBits))
                {
                    var candidate = new PowSolution(chunk.TaskId, nonce);
                    // Только один воркер "выигрывает" задачу.
                    if (Interlocked.CompareExchange(ref solutions[chunk.TaskId], candidate, null) is null)
                    {
                        telemetry?.Solved(chunk.TaskId);
                        int done = Interlocked.Increment(ref completed);
                        long total = Interlocked.Read(ref totalAttempts);
                        progress?.Report(new PowProgress(chunk.TaskId, candidate, done, total));
                    }
                    return;
                }
            }
        }
        finally
        {
            if (telemetry is not null && attempts > reported)
                telemetry.AddAttempts(taskLane, chunk.TaskId, attempts - reported);
            if (taskLane >= 0) telemetry?.EndWork(taskLane);
            Interlocked.Add(ref totalAttempts, attempts);
        }
    }

    private static bool HasLeadingZeroBits(ReadOnlySpan<byte> hash, int difficultyBits)
    {
        int completeBytes = difficultyBits / 8;
        for (int index = 0; index < completeBytes; index++)
            if (hash[index] != 0) return false;
        int remainingBits = difficultyBits % 8;
        return remainingBits == 0 || (hash[completeBytes] >> (8 - remainingBits)) == 0;
    }

    private readonly record struct Chunk(int TaskId, ulong From, ulong To);
}*/


using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DC.Compute;

public sealed class PowSolver : IPowSolver
{
    public const int TaskCount = 100;
    public const int MaximumDifficultyBits = 24;
    public const int MaximumWorkers = TaskCount;

    public Task<PowResult> SolveAsync(string seed, int difficultyBits, int workerCount,
        IProgress<PowProgress>? progress, CancellationToken cancellationToken, PowTelemetry? telemetry = null)
    {
        // 1. Валидация входных параметров (защита от некорректного ввода)
        if (seed is null || seed.Length != 6 || seed.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("The seed must contain exactly six decimal digits (0–9).", nameof(seed));
        if (difficultyBits is < 1 or > MaximumDifficultyBits)
            throw new ArgumentOutOfRangeException(nameof(difficultyBits), "Difficulty must be between 1 and 24 bits.");
        if (workerCount is < 1 or > MaximumWorkers)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be between 1 and 100.");

        // Запускаем всю работу в отдельном потоке (Task.Run), чтобы не блокировать UI-поток приложения
        return Task.Run(() =>
        {
            // Массив для хранения найденных решений. Изначально везде null.
            var solutions = new PowSolution?[TaskCount];
            int completed = 0;
            long totalAttempts = 0;

            // 2. ПОДГОТОВКА ЧАНКОВ (CHUNKING).
            // Вместо того чтобы давать потокам по одной целой задаче (что привело бы к простою в конце),
            // мы нарезаем пространство перебора на мелкие кусочки.
            // chunkSize = примерно 1/32 от среднего ожидаемого количества хешей.
            long chunkSize = Math.Clamp((1L << difficultyBits) / 32, 1L << 12, 1L << 22);
            long rangeEnd = chunkSize * 4096; // Общий диапазон перебора для одной задачи

            // Потокобезопасная очередь (Work-Stealing). 
            // Все потоки будут "растаскивать" чанки отсюда.
            var queue = new ConcurrentQueue<Chunk>();
            for (int taskId = 0; taskId < TaskCount; taskId++)
            {
                for (long from = 0; from < rangeEnd; from += chunkSize)
                {
                    queue.Enqueue(new Chunk(taskId, (ulong)from, (ulong)Math.Min(from + chunkSize, rangeEnd)));
                }
            }

            var elapsed = Stopwatch.StartNew();
            telemetry?.Start(); // Запуск сбора статистики для графиков
            try
            {
                // 3. ЗАПУСК ВОРКЕРОВ (ПОТОКОВ)
                var workers = new Task[workerCount];
                for (int w = 0; w < workerCount; w++)
                {
                    int lane = w; // Фиксируем номер "полосы" для графика загрузки воркеров
                    workers[w] = Task.Run(() =>
                    {
                        // Каждый поток крутится в цикле, пока в очереди есть работа
                        while (queue.TryDequeue(out Chunk chunk))
                        {
                            // Проверка, не нажал ли пользователь "Отмена"
                            if (cancellationToken.IsCancellationRequested) return;

                            // Быстрая проверка: возможно, эту задачу уже решил другой поток
                            if (Volatile.Read(ref solutions[chunk.TaskId]) != null) continue;

                            // Выполнение работы над конкретным чанком
                            SolveChunk(lane, chunk, seed, difficultyBits, solutions,
                                ref completed, ref totalAttempts, progress, telemetry, cancellationToken);
                        }
                    }, cancellationToken);
                }

                // Ждем, пока все потоки закончат работу (очередь опустеет)
                Task.WaitAll(workers);
                elapsed.Stop();

                // Если нас прервали, выбрасываем исключение
                cancellationToken.ThrowIfCancellationRequested();

                // 4. СБОР РЕЗУЛЬТАТОВ
                var finalSolutions = new PowSolution[TaskCount];
                for (int i = 0; i < TaskCount; i++)
                {
                    if (solutions[i] is null)
                        throw new InvalidOperationException($"Task {i} could not be solved within its nonce range.");
                    finalSolutions[i] = solutions[i]!;
                }

                return new PowResult(Array.AsReadOnly(finalSolutions), elapsed.Elapsed,
                    completed, workerCount, totalAttempts);
            }
            finally { telemetry?.Finish(); } // Обязательно закрываем телеметрию
        }, cancellationToken);
    }

    private static void SolveChunk(int lane, in Chunk chunk, string seed, int difficultyBits,
        PowSolution?[] solutions, ref int completed, ref long totalAttempts,
        IProgress<PowProgress>? progress, PowTelemetry? telemetry, CancellationToken cancellationToken)
    {
        // Сообщаем телеметрии, что воркер lane начал работу над задачей chunk.TaskId
        int taskLane = telemetry?.BeginWork(chunk.TaskId, lane) ?? -1;

        // Формируем неизменяемую часть строки (префикс), чтобы не делать это в цикле
        byte[] prefix = Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"BSU-POW-1|{seed}|{chunk.TaskId}|"));

        // Оптимизация памяти: выделяем буферы в стеке (stackalloc), 
        // чтобы сборщик мусора (GC) не тормозил программу миллионами мелких аллокаций.
        Span<byte> message = stackalloc byte[64];
        Span<byte> hash = stackalloc byte[32];
        prefix.AsSpan().CopyTo(message);

        long attempts = 0;
        long reported = 0; // Сколько попыток мы уже отправили в телеметрию

        try
        {
            // ГЛАВНЫЙ ЦИКЛ ПЕРЕБОРА (HOT LOOP)
            for (ulong nonce = chunk.From; nonce < chunk.To; nonce++)
            {
                // Проверки делаем не на каждой итерации (это дорого), а раз в 256 (побитовая магия: & 255)
                if ((nonce & 255UL) == 0)
                {
                    if (cancellationToken.IsCancellationRequested) return;

                    // Каждые 8192 итерации проверяем, не нашел ли ответ для этой задачи другой поток.
                    // Volatile.Read гарантирует, что мы увидим актуальное значение из оперативной памяти.
                    if ((nonce & 8191UL) == 0 && Volatile.Read(ref solutions[chunk.TaskId]) != null) return;
                }

                // Быстро записываем nonce в конец нашей строки (без создания новых string объектов)
                if (!Utf8Formatter.TryFormat(nonce, message[prefix.Length..], out int digits))
                    throw new InvalidOperationException("Could not encode the nonce.");

                // Считаем хеш от получившейся строки байт
                SHA256.HashData(message[..(prefix.Length + digits)], hash);
                attempts = checked(attempts + 1);

                // Отправляем прогресс в телеметрию раз в 8192 хеша (батчинг), чтобы не задушить процессор синхронизацией
                if (telemetry is not null && (attempts & 8191) == 0)
                {
                    telemetry.AddAttempts(taskLane, chunk.TaskId, attempts - reported);
                    reported = attempts;
                }

                // Проверяем, начинается ли хеш с нужного количества нулевых битов
                if (HasLeadingZeroBits(hash, difficultyBits))
                {
                    var candidate = new PowSolution(chunk.TaskId, nonce);

                    // КРИТИЧЕСКИ ВАЖНО: Защита от Race Condition (состояния гонки).
                    // Если два потока одновременно найдут ответ, CompareExchange атомарно запишет только первый.
                    // Второй поток получит не null и просто проигнорирует свой результат.
                    if (Interlocked.CompareExchange(ref solutions[chunk.TaskId], candidate, null) is null)
                    {
                        telemetry?.Solved(chunk.TaskId); // Отмечаем задачу как решенную на графике
                        int done = Interlocked.Increment(ref completed); // Атомарно увеличиваем счетчик
                        long total = Interlocked.Read(ref totalAttempts);
                        progress?.Report(new PowProgress(chunk.TaskId, candidate, done, total));
                    }
                    return; // Задача решена, выходим из цикла перебора
                }
            }
        }
        finally
        {
            // В блоке finally (выполнится даже при выходе из-за отмены) 
            // дописываем оставшиеся непереданные попытки в телеметрию
            if (telemetry is not null && attempts > reported)
                telemetry.AddAttempts(taskLane, chunk.TaskId, attempts - reported);

            if (taskLane >= 0) telemetry?.EndWork(taskLane);

            // Атомарно прибавляем попытки этого чанка к общему счетчику программы
            Interlocked.Add(ref totalAttempts, attempts);
        }
    }

    // Проверка, что первые difficultyBits битов хеша равны нулю
    private static bool HasLeadingZeroBits(ReadOnlySpan<byte> hash, int difficultyBits)
    {
        int completeBytes = difficultyBits / 8; // Количество полных нулевых байтов
        for (int index = 0; index < completeBytes; index++)
            if (hash[index] != 0) return false;

        int remainingBits = difficultyBits % 8; // Остаток битов
        return remainingBits == 0 || (hash[completeBytes] >> (8 - remainingBits)) == 0;
    }

    // Структура, описывающая кусочек работы: для какой задачи и с какого по какое число перебирать
    private readonly record struct Chunk(int TaskId, ulong From, ulong To);
}