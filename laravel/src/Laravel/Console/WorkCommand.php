<?php

namespace Zouriel\Workflows\Laravel\Console;

use Illuminate\Console\Command;
use Throwable;
use Zouriel\Workflows\Laravel\Worker;

/**
 * The background worker, to run under Supervisor or systemd like `queue:work`: rounds of outbox, timers and cleanup,
 * sleeping when a round found nothing. It stops on SIGTERM/SIGINT after the current round, and after `--max-time`
 * seconds or above `--memory` MB, so the process manager starts a fresh one.
 */
final class WorkCommand extends Command
{
    protected $signature = 'workflows:work
        {--sleep= : Seconds to sleep when nothing was due (default: workflows.worker.sleep)}
        {--max-time=0 : Stop after this many seconds (0 = never)}
        {--max-rounds=0 : Stop after this many rounds (0 = never)}
        {--memory=256 : Stop when the process uses more than this many MB}';

    protected $description = 'Run the workflow worker: committed events, due timers and cleanup, until stopped';

    private bool $stop = false;

    public function handle(Worker $worker): int
    {
        if (extension_loaded('pcntl')) {
            $this->trap([SIGTERM, SIGINT], fn () => $this->stop = true);
        }
        $sleep = (float) ($this->option('sleep') ?? config('workflows.worker.sleep', 1));
        $maxTime = (int) $this->option('max-time');
        $maxRounds = (int) $this->option('max-rounds');
        $started = time();
        for ($round = 1; ! $this->stop; $round++) {
            try {
                $handled = $worker->round();
            } catch (Throwable $e) {
                // A database outage or a bug in one round must not end the worker; it is reported and retried.
                report($e);
                $handled = 0;
            }
            if (($maxRounds > 0 && $round >= $maxRounds) || ($maxTime > 0 && time() - $started >= $maxTime)
                || memory_get_usage(true) > (int) $this->option('memory') * 1048576) {
                break;
            }
            if ($handled === 0 && $sleep > 0) {
                usleep((int) ($sleep * 1000000));
            }
        }

        return self::SUCCESS;
    }
}
