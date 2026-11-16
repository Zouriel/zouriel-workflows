<?php

namespace Zouriel\Workflows\Tests\Support\App;

use Illuminate\Database\Eloquent\Model;
use Spatie\Translatable\HasTranslations;
use Zouriel\Workflows\Laravel\Changes\HasWorkflows;

/** A programme with a bilingual name (spatie translatable JSON). */
class Programme extends Model
{
    use HasTranslations, HasWorkflows;

    protected $guarded = [];

    public array $translatable = ['name'];

    public $timestamps = false;
}
