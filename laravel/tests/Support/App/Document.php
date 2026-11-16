<?php

namespace Zouriel\Workflows\Tests\Support\App;

use Illuminate\Database\Eloquent\Concerns\HasUuids;
use Illuminate\Database\Eloquent\Model;
use Zouriel\Workflows\Laravel\Changes\HasWorkflows;

class Document extends Model
{
    use HasUuids, HasWorkflows;

    protected $guarded = [];

    public $timestamps = false;

    protected function casts(): array
    {
        return ['verified' => 'boolean'];
    }
}
