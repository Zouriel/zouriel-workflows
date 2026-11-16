<?php

namespace Zouriel\Workflows\Tests\Support\App;

/** A subclass in the same table: its writes are writes of an application. */
class UrgentApplication extends Application
{
    protected $table = 'applications';
}
