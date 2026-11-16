<?php

namespace Zouriel\Workflows\Tests\Support;

use Illuminate\Database\ConnectionInterface;
use Illuminate\Database\Schema\Blueprint;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Changes\ChangeInspector;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\Storage\WorkflowSession;
use Zouriel\Workflows\Core\Testing\HostStore;
use Zouriel\Workflows\Core\Time\Clock;
use Zouriel\Workflows\Laravel\Storage\DatabaseSession;
use Zouriel\Workflows\Laravel\Storage\DatabaseStore;

/** The database store with the fixtures' records in a table (`TableGateway`), driven by the test host and the conformance runner. */
final class DatabaseHostStore implements HostStore
{
    public readonly DatabaseStore $inner;

    private ?ChangeInspector $changes = null;

    /** @var array<string, true> */
    private array $refused = [];

    public function __construct(ConnectionInterface $db, WorkflowRegistry $registry, Clock $clock, string $prefix = 'wf_')
    {
        $this->inner = new DatabaseStore($db, $clock, fn (DatabaseSession $s) => new TableGateway($s, $registry, $this->changes, $this->refused), $prefix);
    }

    /** Creates the records table next to the migrated workflow tables. */
    public static function createRecordsTable(ConnectionInterface $db, string $prefix = 'wf_'): void
    {
        $db->getSchemaBuilder()->create("{$prefix}test_records", function (Blueprint $t) {
            $t->string('entity', 128);
            $t->string('id', 191);
            $t->bigInteger('ord');
            $t->longText('json');
            $t->primary(['entity', 'id']);
        });
    }

    public function begin(): WorkflowSession
    {
        return $this->inner->begin();
    }

    public function attach(WorkflowEngine $engine): void
    {
        $this->changes = $engine->changes;
    }

    private function inUnit(callable $work): void
    {
        $session = $this->inner->begin();
        try {
            $work($session->entities());
            $session->commit();
        } finally {
            $session->close();
        }
    }

    public function createRecord(string $entity, string $id, stdClass $values): void
    {
        $this->inUnit(fn (TableGateway $g) => $g->createWithId($entity, $id, $values));
    }

    public function changeRecord(string $entity, string $id, stdClass $values): void
    {
        $this->inUnit(fn (TableGateway $g) => $g->setFields($entity, $id, $values));
    }

    public function deleteRecord(string $entity, string $id): void
    {
        $this->inUnit(fn (TableGateway $g) => $g->delete($entity, $id));
    }

    public function peekRecord(string $entity, string $id): ?RecordSnapshot
    {
        $json = $this->inner->table('test_records')->where('entity', $entity)->where('id', $id)->value('json');

        return $json === null ? null : new RecordSnapshot($entity, $id, Json::decode($json) ?? new stdClass);
    }

    public function all(string $table): array
    {
        return $this->inner->all($table);
    }

    public function refuseWrites(string $field, bool $refuse): void
    {
        if ($refuse) {
            $this->refused[$field] = true;
        } else {
            unset($this->refused[$field]);
        }
    }
}
