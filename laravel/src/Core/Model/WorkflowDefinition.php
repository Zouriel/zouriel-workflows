<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A workflow definition (spec §1–§7), parsed from the JSON the builder writes. Port of .NET `Model/Definition.cs` `WorkflowDefinition`. */
final class WorkflowDefinition
{
    /** The spec version the definition was written for (currently 1). */
    public int $specVersion = 1;

    /** Immutable generated id (`[A-Za-z0-9_-]{1,64}`). */
    public string $id = '';

    /** Editable unique key that people, APIs and `subWorkflow` steps use. */
    public string $key = '';

    /** `automation` or `lifecycle` (`WorkflowKinds`). */
    public string $kind = WorkflowKinds::AUTOMATION;

    public string $title = '';

    public ?string $description = null;

    /** The published version stamped on a published copy; null in a draft never published. */
    public ?int $revision = null;

    /** The registry entity the workflow is about; null for schedules and signals with no record. */
    public ?string $entity = null;

    /** What starts an automation; null for lifecycles. */
    public ?Trigger $trigger = null;

    /** @var list<Step>|null the steps an automation runs */
    public ?array $steps = null;

    public ?Lifecycle $lifecycle = null;

    /** @var list<Variable>|null */
    public ?array $variables = null;

    public ?WorkflowSettings $settings = null;

    public ?PublicProcessingTime $publicProcessingTime = null;

    public function isLifecycle(): bool
    {
        return $this->kind === WorkflowKinds::LIFECYCLE;
    }

    public static function fromJson(stdClass $value, string $path = '$'): self
    {
        $definition = new self;
        $definition->specVersion = Read::int($value, 'specVersion', $path, 1);
        $definition->id = Read::string($value, 'id', $path);
        $definition->key = Read::string($value, 'key', $path);
        $definition->kind = Read::string($value, 'kind', $path, WorkflowKinds::AUTOMATION);
        $definition->title = Read::string($value, 'title', $path);
        $definition->description = Read::optionalString($value, 'description', $path);
        $definition->revision = Read::optionalInt($value, 'revision', $path);
        $definition->entity = Read::optionalString($value, 'entity', $path);
        $definition->trigger = Read::model($value, 'trigger', $path, 'Zouriel.Workflows.Model.Trigger', Trigger::fromJson(...));
        $definition->steps = Step::list($value, 'steps', $path);
        $definition->lifecycle = Read::model($value, 'lifecycle', $path, 'Zouriel.Workflows.Model.Lifecycle', Lifecycle::fromJson(...));
        $definition->variables = Read::list($value, 'variables', $path, Variable::fromJson(...));
        $definition->settings = Read::model($value, 'settings', $path, 'Zouriel.Workflows.Model.WorkflowSettings', WorkflowSettings::fromJson(...));
        $definition->publicProcessingTime = Read::model($value, 'publicProcessingTime', $path, 'Zouriel.Workflows.Model.PublicProcessingTime', PublicProcessingTime::fromJson(...));

        return $definition;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'specVersion' => $this->specVersion, 'id' => $this->id, 'key' => $this->key, 'kind' => $this->kind, 'title' => $this->title,
            'description' => $this->description, 'revision' => $this->revision, 'entity' => $this->entity, 'trigger' => $this->trigger?->toJson(),
            'steps' => Write::list($this->steps), 'lifecycle' => $this->lifecycle?->toJson(), 'variables' => Write::list($this->variables),
            'settings' => $this->settings?->toJson(), 'publicProcessingTime' => $this->publicProcessingTime?->toJson(),
        ]);
    }
}
