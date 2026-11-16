<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/**
 * One page of a list, with the total number of matches.
 *
 * @template T
 */
final readonly class Page
{
    /** @param  list<T>  $items */
    public function __construct(public array $items, public int $total) {}
}
