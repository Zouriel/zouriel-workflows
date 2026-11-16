<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Settings of one notification channel. Port of .NET `ChannelSettings`. */
final class ChannelSettings
{
    /** False switches the channel off. */
    public ?bool $enabled = null;

    /** Text appended to every message on the channel. */
    public ?string $footer = null;

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Settings.ChannelSettings');
        $channel = new self;
        $channel->enabled = Read::optionalBool($value, 'enabled', $path);
        $channel->footer = Read::optionalString($value, 'footer', $path);

        return $channel;
    }

    public function toJson(): stdClass
    {
        return Write::object(['enabled' => $this->enabled, 'footer' => $this->footer]);
    }
}
