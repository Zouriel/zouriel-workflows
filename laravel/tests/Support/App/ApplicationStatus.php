<?php

namespace Zouriel\Workflows\Tests\Support\App;

enum ApplicationStatus: string
{
    case Draft = 'Draft';
    case Submitted = 'Submitted';
    case Approved = 'Approved';
    case Rejected = 'Rejected';
}
