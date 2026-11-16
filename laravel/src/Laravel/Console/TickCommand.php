<?php

namespace Zouriel\Workflows\Laravel\Console;

use Illuminate\Console\Command;
use Zouriel\Workflows\Laravel\Worker;

/**
 * One round of background work, for hosts without a long-running worker:
 * `Schedule::command('workflows:tick --until-idle')->everyMinute()->withoutOverlapping()`. Timers then fire up to a
 * minute late.
 */
final class TickCommand extends Command
{
    protected $signature = 'workflows:tick {--until-idle : Keep going until a round finds nothing to do (at most 100 rounds)}';

    protected $description = 'Run one round of workflow background work (events, due timers, cleanup)';

    public function handle(Worker $worker): int
    {
        $total = 0;
        for ($round = 0; $round < ($this->option('until-idle') ? 100 : 1); $round++) {
            $handled = $worker->round();
            $total += $handled;
            if ($handled === 0) {
                break;
            }
        }
        $this->info("Handled {$total} event(s) and timer(s).");

        return self::SUCCESS;
    }
}
