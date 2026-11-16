<?php

namespace Zouriel\Workflows\Core\Registry;

use BackedEnum;
use LogicException;
use UnitEnum;
use Zouriel\Workflows\Core\Json\Values;

/** Describes one field: its key, label, type and what workflows may do with it. Port of .NET `FieldBuilder`. */
final class FieldBuilder
{
    private string $key;

    private string $label;

    /** @var list<FieldOption>|null */
    private ?array $options = null;

    private ?bool $watchable = null;

    private ?bool $settable = null;

    private ?bool $lifecycle = null;

    private ?bool $sensitive = null;

    private ?bool $readable = null;

    private ?bool $required = null;

    public function __construct(private readonly string $attribute, private string $type)
    {
        $this->key = $attribute;
        $this->label = RegistryBuilder::humanize($attribute);
    }

    /** The key token paths use (default: the attribute name). */
    public function key(string $key): self
    {
        $this->key = $key;

        return $this;
    }

    /** The name shown to people (default: humanised from the attribute name). */
    public function label(string $label): self
    {
        $this->label = $label;

        return $this;
    }

    /** Sets the field type (`FieldTypes`). */
    public function type(string $type): self
    {
        $this->type = $type;

        return $this;
    }

    public function text(): self
    {
        return $this->type(FieldTypes::TEXT);
    }

    public function longText(): self
    {
        return $this->type(FieldTypes::LONG_TEXT);
    }

    public function email(): self
    {
        return $this->type(FieldTypes::EMAIL);
    }

    public function phone(): self
    {
        return $this->type(FieldTypes::PHONE);
    }

    public function url(): self
    {
        return $this->type(FieldTypes::URL);
    }

    public function number(): self
    {
        return $this->type(FieldTypes::NUMBER);
    }

    public function currency(): self
    {
        return $this->type(FieldTypes::CURRENCY);
    }

    /** True or false (.NET infers it from a `bool` property). */
    public function boolean(): self
    {
        return $this->type(FieldTypes::BOOLEAN);
    }

    /** A calendar date (`YYYY-MM-DD`). */
    public function date(): self
    {
        return $this->type(FieldTypes::DATE);
    }

    /** An instant (ISO 8601 UTC). */
    public function dateTime(): self
    {
        return $this->type(FieldTypes::DATE_TIME);
    }

    /** The field holds a user id (assignable, shown as the user's name). */
    public function user(): self
    {
        return $this->type(FieldTypes::USER);
    }

    /** Free JSON (.NET's type for properties it cannot map). */
    public function json(): self
    {
        return $this->type(FieldTypes::JSON);
    }

    /**
     * An enum field with options from a PHP enum: a backed enum stores its values, a pure enum its case
     * names; labels are humanised case names.
     *
     * @param  class-string<UnitEnum>  $enum
     */
    public function enum(string $enum): self
    {
        $this->type = FieldTypes::ENUM;
        $this->options = array_map(fn (UnitEnum $case) => new FieldOption(
            $case instanceof BackedEnum ? $case->value : $case->name,
            RegistryBuilder::humanize($case->name),
        ), $enum::cases());

        return $this;
    }

    /**
     * An enum field with explicit stored values and labels.
     *
     * @param  array<string, string>  $options  stored value => label
     */
    public function options(array $options): self
    {
        $this->type = FieldTypes::ENUM;
        $this->options = array_map(fn ($value, $label) => new FieldOption((string) $value, $label), array_keys($options), $options);

        return $this;
    }

    /** Changes the label of one option. */
    public function optionLabel(string $value, string $label): self
    {
        foreach ($this->options ?? [] as $i => $option) {
            if (Values::text($option->value) === $value) {
                $this->options[$i] = new FieldOption($option->value, $label);
                break;
            }
        }

        return $this;
    }

    /** Triggers and condition waits may watch this field. */
    public function watchable(bool $on = true): self
    {
        $this->watchable = $on;

        return $this;
    }

    /** `setFields` steps may write this field. */
    public function settable(bool $on = true): self
    {
        $this->settable = $on;

        return $this;
    }

    /** A lifecycle may own this enum field. Implies watchable. */
    public function lifecycleCapable(bool $on = true): self
    {
        $this->lifecycle = $on;
        $this->watchable ??= $on;

        return $this;
    }

    /** Masked in audit entries and run views. */
    public function sensitive(bool $on = true): self
    {
        $this->sensitive = $on;

        return $this;
    }

    /** Hides the field from tokens (`readable: false`). */
    public function hidden(): self
    {
        $this->readable = false;

        return $this;
    }

    /** Makes the field explicitly readable in tokens (the default). */
    public function readable(): self
    {
        $this->readable = true;

        return $this;
    }

    /** The app requires a value. */
    public function required(bool $on = true): self
    {
        $this->required = $on;

        return $this;
    }

    /** @throws LogicException a lifecycle-capable field that is not an enum */
    public function build(?string $entity = null): FieldDescriptor
    {
        if ($this->lifecycle === true && $this->type !== FieldTypes::ENUM) {
            throw new LogicException("Field '{$this->key}' is marked lifecycleCapable but is not an enum field.");
        }

        return new FieldDescriptor(
            key: $this->key, label: $this->label, type: $this->type, options: $this->options, entity: $entity,
            readable: $this->readable, watchable: $this->watchable, settable: $this->settable, lifecycle: $this->lifecycle,
            sensitive: $this->sensitive, required: $this->required, attribute: $this->attribute,
        );
    }
}
