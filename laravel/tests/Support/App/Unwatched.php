<?php

namespace Zouriel\Workflows\Tests\Support\App;

use Illuminate\Database\Eloquent\Model;

/** A model without HasWorkflows: registering it as an entity is refused. */
class Unwatched extends Model {}
