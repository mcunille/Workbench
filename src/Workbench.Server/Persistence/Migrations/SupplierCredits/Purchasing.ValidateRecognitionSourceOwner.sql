-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ValidateRecognitionSourceOwner
  @TenantId uniqueidentifier,@ActorId uniqueidentifier,@SessionId uniqueidentifier,@EventId uniqueidentifier,
  @Replacement nvarchar(max),@Facts nvarchar(max) OUTPUT
WITH EXECUTE AS OWNER
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Source validation requires the accounting owner transaction.',1;
  DECLARE @PostingPermission nvarchar(100),@CorrectionPermission nvarchar(100),@Schema sysname,@Module sysname,
    @DefinitionHash binary(32),@ObjectId int,@Definition nvarchar(max),@Invocation nvarchar(max),@OwnerFacts nvarchar(max);
  -- The original event's permission was supplied by its private trusted producer,
  -- never by the bill correction JSON. Only deployment-owned catalog entries can bind it.
  SELECT @PostingPermission=registered.PostingPermission,@CorrectionPermission=registered.CorrectionPermission,
      @Schema=registered.ValidatorSchema,@Module=registered.ValidatorName,@DefinitionHash=registered.DefinitionSha256
    FROM Purchasing.RecognitionSideEvents actual
    JOIN Purchasing.RecognitionSourceOwners registered WITH(UPDLOCK,HOLDLOCK) ON registered.PostingPermission=actual.EvidenceMutationPermission COLLATE Latin1_General_100_BIN2
    WHERE actual.TenantId=@TenantId AND actual.Id=@EventId;
  IF @Module IS NULL THROW 51004,'The recognition source has no registered complete correction owner.',1;
  BEGIN TRY
    EXEC Accounting.RequirePermission @ActorId,@SessionId,@CorrectionPermission;
    IF @Replacement IS NOT NULL EXEC Accounting.RequirePermission @ActorId,@SessionId,@PostingPermission;
  END TRY BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current upstream source authority is required.',1; THROW; END CATCH;
  SET @ObjectId=OBJECT_ID(QUOTENAME(@Schema)+N'.'+QUOTENAME(@Module),N'P');
  SET @Definition=OBJECT_DEFINITION(@ObjectId);
  IF @ObjectId IS NULL OR @Definition IS NULL OR @DefinitionHash<>HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Definition))
    THROW 51009,'The registered source owner module changed.',1;
  SET @Invocation=N'EXEC '+QUOTENAME(@Schema)+N'.'+QUOTENAME(@Module)+N' @TenantId,@EventId,@Replacement,@Facts OUTPUT;';
  EXEC sys.sp_executesql @Invocation,
    N'@TenantId uniqueidentifier,@EventId uniqueidentifier,@Replacement nvarchar(max),@Facts nvarchar(max) OUTPUT',
    @TenantId,@EventId,@Replacement,@OwnerFacts OUTPUT;
  IF @OwnerFacts IS NULL OR ISJSON(@OwnerFacts,OBJECT)<>1 OR DATALENGTH(@OwnerFacts)>262144
    THROW 51004,'The source owner did not supply complete current facts.',1;
  SET @Facts=(SELECT @PostingPermission postingPermission,@CorrectionPermission correctionPermission,@Schema validatorSchema,@Module validatorName,
    CONVERT(varchar(64),@DefinitionHash,2) validatorSha256,JSON_QUERY(@OwnerFacts) facts FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
END;
