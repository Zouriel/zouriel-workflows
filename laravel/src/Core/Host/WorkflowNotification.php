<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;
use Zouriel\Forms\Json;

/**
 * A notification for the host to deliver (spec §7 `notify`, and task messages). `idempotencyKey` is
 * stable across redelivery; senders should drop repeats with the same key. Port of .NET `WorkflowNotification`.
 */
final readonly class WorkflowNotification
{
    /** @param  list<NotificationRecipient>  $to */
    public function __construct(
        public string $channel,
        public array $to,
        public ?string $subject,
        public string $body,
        public ?string $link,
        public string $instanceId,
        public ?string $entity,
        public ?string $entityId,
        public string $idempotencyKey,
    ) {}

    /** The outbox payload. */
    public function toJson(): string
    {
        return Json::encode((object) [
            'channel' => $this->channel, 'to' => array_map(fn (NotificationRecipient $r) => $r->toJson(), $this->to), 'subject' => $this->subject,
            'body' => $this->body, 'link' => $this->link, 'instanceId' => $this->instanceId, 'entity' => $this->entity, 'entityId' => $this->entityId,
            'idempotencyKey' => $this->idempotencyKey,
        ]);
    }

    public static function fromJson(string $json): self
    {
        /** @var stdClass $v */
        $v = Json::decode($json);

        return new self($v->channel, array_map(NotificationRecipient::fromJson(...), $v->to ?? []), $v->subject ?? null, $v->body ?? '',
            $v->link ?? null, $v->instanceId ?? '', $v->entity ?? null, $v->entityId ?? null, $v->idempotencyKey ?? '');
    }
}
