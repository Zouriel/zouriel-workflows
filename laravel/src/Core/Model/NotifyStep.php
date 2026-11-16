<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Queues a message on a channel through the outbox (output `sent`). Port of .NET `NotifyStep`. */
final class NotifyStep extends Step
{
    /** The registry channel key. */
    public string $channel = '';

    /** @var list<Assignee> people (spec §8) and `{email}` address templates */
    public array $to = [];

    public ?Template $subject = null;

    public Template $body;

    /** `record` for a link to the record, or a URL template. */
    public ?Template $link = null;

    public function __construct()
    {
        $this->body = new Template;
    }

    protected function read(stdClass $value, string $path): void
    {
        $this->channel = Read::string($value, 'channel', $path);
        $this->to = Assignee::list($value, 'to', $path) ?? [];
        $this->subject = Template::fromJson(Read::value($value, 'subject'), "{$path}.subject");
        $this->body = Template::fromJson(Read::value($value, 'body'), "{$path}.body") ?? new Template;
        $this->link = Template::fromJson(Read::value($value, 'link'), "{$path}.link");
    }

    protected function members(): array
    {
        return [
            'channel' => $this->channel, 'to' => Write::list($this->to), 'subject' => $this->subject?->toJson(),
            'body' => $this->body->toJson(), 'link' => $this->link?->toJson(),
        ];
    }
}
