-- Copyright (c) 2026 The White Stag Collection.
-- Frozen GEM-01 distribution, reviewed 2026-10-02: four entries, nineteen assertions.
-- Historical migration input: do not regenerate from future catalog revisions.
DECLARE @Pilot TABLE
(
    Id uniqueidentifier PRIMARY KEY, MaterialKind nvarchar(20), CommonName nvarchar(200),
    [Group] nvarchar(200), Species nvarchar(200), Variety nvarchar(200), Description nvarchar(2000),
    IdentityKey binary(32)
);
INSERT @Pilot VALUES(N'357a74d0-721e-4738-b9fa-14710e7b2385',N'mineral',N'Diamond',NULL,N'Diamond',NULL,N'Diamond is a mineral form of carbon.',0xDC71C9B96D28A2B0C83032E43C0A7E523E6224892989B575492573B0F7D4DAC2),
(N'e548c296-c07f-712c-a424-850c7d7e2829',N'mineral',N'Sapphire',NULL,N'Corundum',N'Sapphire',N'Sapphire includes corundum gems in colors other than those classified as ruby.',0x79DC54945F3B7E96756E49252D9978DC5ACC9805D62D9AC4D3DA6E5AA8F0B8A6),
(N'708601a7-c715-cf9d-5b95-a6782c3d7e29',N'mineral',N'Emerald',NULL,N'Beryl',N'Emerald',N'Emerald is the green to bluish green gem variety of beryl.',0x7B41DC5BD3DEE181B943A724F8339A4132B5304385B8197D353EE930A96F8DF1),
(N'1536bf85-f264-a21f-5662-1e5b64a001d6',N'mineral',N'Ruby',NULL,N'Corundum',N'Ruby',N'Ruby is the red gem variety of corundum.',0xC16862FC33E8E88B350A3204B1AD1E48B25EB5DBD92F2C06BB236B046179AAB2);
IF EXISTS (
    SELECT 1 FROM @Pilot pilot
    JOIN Gemology.Entries existing ON existing.IdentityKey=pilot.IdentityKey AND existing.IsRetired=0
    WHERE existing.Id<>pilot.Id AND NOT EXISTS (SELECT 1 FROM Gemology.Entries retained WHERE retained.Id=pilot.Id)
)
    THROW 50020,'Pilot gem identity conflicts with published content. Resolve through reviewed forward correction.',1;

DECLARE @Installed TABLE (Id uniqueidentifier PRIMARY KEY);
INSERT Gemology.Entries(Id,MaterialKind,CommonName,[Group],Species,Variety,Description,IdentityKey,IsRetired)
OUTPUT inserted.Id INTO @Installed
SELECT pilot.Id,pilot.MaterialKind,pilot.CommonName,pilot.[Group],pilot.Species,pilot.Variety,pilot.Description,pilot.IdentityKey,0
FROM @Pilot pilot
WHERE NOT EXISTS (SELECT 1 FROM Gemology.Entries retained WHERE retained.Id=pilot.Id);

DECLARE @Sources TABLE
(
    Id uniqueidentifier PRIMARY KEY, EntryId uniqueidentifier, Field nvarchar(30), Title nvarchar(200),
    Publisher nvarchar(200), Url nvarchar(2000), Citation nvarchar(2000), AccessedOn date, ReviewedOn date
);
INSERT @Sources VALUES(N'04e43078-8b4a-a891-6a87-6c42ed8fd890',N'357a74d0-721e-4738-b9fa-14710e7b2385',N'materialKind',N'Diamond Description',N'Gemological Institute of America',N'https://www.gia.edu/diamond-description',N'Opening discussion of chemical composition and crystal structure.',N'2026-10-02',N'2026-10-02'),
(N'86e1b1ef-9366-967c-3fa9-e32b7008ab03',N'357a74d0-721e-4738-b9fa-14710e7b2385',N'commonName',N'Diamond Description',N'Gemological Institute of America',N'https://www.gia.edu/diamond-description',N'Opening discussion of chemical composition and crystal structure.',N'2026-10-02',N'2026-10-02'),
(N'cc93af01-65d1-f7e8-01dd-11679e9ef549',N'357a74d0-721e-4738-b9fa-14710e7b2385',N'species',N'Diamond Description',N'Gemological Institute of America',N'https://www.gia.edu/diamond-description',N'Opening discussion of chemical composition and crystal structure.',N'2026-10-02',N'2026-10-02'),
(N'd8efbb6e-36dd-893c-94b5-e2b9d29f58ae',N'357a74d0-721e-4738-b9fa-14710e7b2385',N'description',N'Diamond Description',N'Gemological Institute of America',N'https://www.gia.edu/diamond-description',N'Opening discussion of chemical composition and crystal structure.',N'2026-10-02',N'2026-10-02'),
(N'aa1d548d-cc55-7da5-a231-1d76c90b912d',N'e548c296-c07f-712c-a424-850c7d7e2829',N'materialKind',N'Sapphire Description',N'Gemological Institute of America',N'https://www.gia.edu/sapphire-description',N'Opening definition of sapphire and the discussion of fancy sapphire colors.',N'2026-10-02',N'2026-10-02'),
(N'026f3b21-7735-1ec2-1cb4-f59c2d431289',N'e548c296-c07f-712c-a424-850c7d7e2829',N'commonName',N'Sapphire Description',N'Gemological Institute of America',N'https://www.gia.edu/sapphire-description',N'Opening definition of sapphire and the discussion of fancy sapphire colors.',N'2026-10-02',N'2026-10-02'),
(N'cb8f69f8-37f5-2591-0779-f3d44a4bc894',N'e548c296-c07f-712c-a424-850c7d7e2829',N'species',N'Sapphire Description',N'Gemological Institute of America',N'https://www.gia.edu/sapphire-description',N'Opening definition of sapphire and the discussion of fancy sapphire colors.',N'2026-10-02',N'2026-10-02'),
(N'1664f568-1538-db30-98d6-93b540d8e4d0',N'e548c296-c07f-712c-a424-850c7d7e2829',N'description',N'Sapphire Description',N'Gemological Institute of America',N'https://www.gia.edu/sapphire-description',N'Opening definition of sapphire and the discussion of fancy sapphire colors.',N'2026-10-02',N'2026-10-02'),
(N'd7ea7a51-2684-8118-dbf3-1b4f40ad9881',N'e548c296-c07f-712c-a424-850c7d7e2829',N'variety',N'Sapphire Description',N'Gemological Institute of America',N'https://www.gia.edu/sapphire-description',N'Opening definition of sapphire and the discussion of fancy sapphire colors.',N'2026-10-02',N'2026-10-02'),
(N'b144c3c1-aa8a-11ed-558f-fbba10775bf0',N'708601a7-c715-cf9d-5b95-a6782c3d7e29',N'materialKind',N'Emerald Description',N'Gemological Institute of America',N'https://www.gia.edu/emerald-description',N'Opening classification sentence; following paragraphs explain the distinction from lighter green beryl.',N'2026-10-02',N'2026-10-02'),
(N'3660f1d4-cffd-3964-1c5b-34dca612e0a1',N'708601a7-c715-cf9d-5b95-a6782c3d7e29',N'commonName',N'Emerald Description',N'Gemological Institute of America',N'https://www.gia.edu/emerald-description',N'Opening classification sentence; following paragraphs explain the distinction from lighter green beryl.',N'2026-10-02',N'2026-10-02'),
(N'ae533061-aa4d-ec93-2ead-de9e4b4b161b',N'708601a7-c715-cf9d-5b95-a6782c3d7e29',N'species',N'Emerald Description',N'Gemological Institute of America',N'https://www.gia.edu/emerald-description',N'Opening classification sentence; following paragraphs explain the distinction from lighter green beryl.',N'2026-10-02',N'2026-10-02'),
(N'802f3079-c740-ea2b-463d-3f8e2f7deb62',N'708601a7-c715-cf9d-5b95-a6782c3d7e29',N'description',N'Emerald Description',N'Gemological Institute of America',N'https://www.gia.edu/emerald-description',N'Opening classification sentence; following paragraphs explain the distinction from lighter green beryl.',N'2026-10-02',N'2026-10-02'),
(N'e312ee9a-6d0c-e703-7172-7343b0f44245',N'708601a7-c715-cf9d-5b95-a6782c3d7e29',N'variety',N'Emerald Description',N'Gemological Institute of America',N'https://www.gia.edu/emerald-description',N'Opening classification sentence; following paragraphs explain the distinction from lighter green beryl.',N'2026-10-02',N'2026-10-02'),
(N'aaec3dc7-ecb6-c6ae-c5ed-fb2f9735e369',N'1536bf85-f264-a21f-5662-1e5b64a001d6',N'materialKind',N'Ruby Description',N'Gemological Institute of America',N'https://www.gia.edu/ruby-description',N'Opening classification paragraph and the following discussion of red coloration.',N'2026-10-02',N'2026-10-02'),
(N'2752f882-9be1-04cb-8299-cfa937814f9c',N'1536bf85-f264-a21f-5662-1e5b64a001d6',N'commonName',N'Ruby Description',N'Gemological Institute of America',N'https://www.gia.edu/ruby-description',N'Opening classification paragraph and the following discussion of red coloration.',N'2026-10-02',N'2026-10-02'),
(N'e0d3e019-2cc6-e369-ee84-c54d4e2cfb23',N'1536bf85-f264-a21f-5662-1e5b64a001d6',N'species',N'Ruby Description',N'Gemological Institute of America',N'https://www.gia.edu/ruby-description',N'Opening classification paragraph and the following discussion of red coloration.',N'2026-10-02',N'2026-10-02'),
(N'b7008f62-1d61-0852-8fe1-613362e1dc1e',N'1536bf85-f264-a21f-5662-1e5b64a001d6',N'description',N'Ruby Description',N'Gemological Institute of America',N'https://www.gia.edu/ruby-description',N'Opening classification paragraph and the following discussion of red coloration.',N'2026-10-02',N'2026-10-02'),
(N'381d7751-9c00-b922-2a02-926b017c70f8',N'1536bf85-f264-a21f-5662-1e5b64a001d6',N'variety',N'Ruby Description',N'Gemological Institute of America',N'https://www.gia.edu/ruby-description',N'Opening classification paragraph and the following discussion of red coloration.',N'2026-10-02',N'2026-10-02');
-- Never backfill or replace assertions on a retained (including retired) entry.
INSERT Gemology.SourceAssertions(Id,EntryId,Field,Title,Publisher,Url,Citation,AccessedOn,ReviewedOn)
SELECT source.Id,source.EntryId,source.Field,source.Title,source.Publisher,source.Url,source.Citation,source.AccessedOn,source.ReviewedOn
FROM @Sources source JOIN @Installed installed ON installed.Id=source.EntryId;
-- The reviewed package has no aliases or notable-locality assertions; absence is intentional.
