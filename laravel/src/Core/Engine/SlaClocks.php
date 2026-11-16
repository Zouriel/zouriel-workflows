<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * Reads and writes the kept SLA clocks of a record instance, kept in its `data` JSON as
 * `{"sla":{"STATE_ID":{...}}}`, so a record that never pauses keeps `{}`. Port of .NET `Engine/SlaClocks.cs`.
 */
final class SlaClocks
{
    /** @return array<string, SlaClock> */
    public static function read(InstanceRecord $record): array
    {
        $result = [];
        if (trim($record->data) === '' || $record->data === '{}') {
            return $result;
        }
        $root = Json::decode($record->data);
        if (! $root instanceof stdClass || ! ($root->sla ?? null) instanceof stdClass) {
            return $result;
        }
        foreach (get_object_vars($root->sla) as $stateId => $o) {
            if (! $o instanceof stdClass) {
                continue;
            }
            $result[(string) $stateId] = new SlaClock((int) ($o->leftMs ?? 0), (bool) ($o->working ?? false), $o->reasons ?? [], (bool) ($o->ran ?? false),
                is_string($o->due ?? null) ? Instant::parse($o->due) : null);
        }

        return $result;
    }

    /** Stores the clocks (none: the record's data goes back to `{}`). Does not save the instance. @param  array<string, SlaClock>  $clocks */
    public static function write(InstanceRecord $record, array $clocks): void
    {
        $root = trim($record->data) === '' || $record->data === '{}' ? new stdClass : (Json::decode($record->data) ?? new stdClass);
        if (! $root instanceof stdClass) {
            $root = new stdClass;
        }
        unset($root->sla);
        if ($clocks !== []) {
            ksort($clocks, SORT_STRING);
            $o = new stdClass;
            foreach ($clocks as $stateId => $c) {
                $n = new stdClass;
                if ($c->ran) {
                    $n->ran = true;
                } elseif ($c->due !== null) {
                    $n->due = Instant::format($c->due);
                } else {
                    $n->leftMs = $c->leftMs;
                    $n->working = $c->working;
                }
                if ($c->reasons !== []) {
                    $n->reasons = $c->reasons;
                }
                $o->{$stateId} = $n;
            }
            $root->sla = $o;
        }
        $record->data = get_object_vars($root) === [] ? '{}' : Json::encode($root);
    }

    /** Drops every kept clock of the record (force-state, reset, out-of-sync, migrate, cancel). */
    public static function clear(InstanceRecord $record): void
    {
        if (strlen($record->data) > 2 && self::read($record) !== []) {
            self::write($record, []);
        }
    }
}
