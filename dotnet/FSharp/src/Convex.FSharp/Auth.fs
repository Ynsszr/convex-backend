namespace Convex.FSharp

/// Standard OIDC claims plus the provider's additional JSON claims. Authentication
/// remains the backend's responsibility; this type does not grant authorization.
type UserIdentity =
    { TokenIdentifier:string
      Subject:string
      Issuer:string
      Name:string option
      GivenName:string option
      FamilyName:string option
      Nickname:string option
      PreferredUsername:string option
      ProfileUrl:string option
      PictureUrl:string option
      Email:string option
      EmailVerified:bool option
      Gender:string option
      Birthday:string option
      Timezone:string option
      Language:string option
      PhoneNumber:string option
      PhoneNumberVerified:bool option
      Address:string option
      UpdatedAt:string option
      CustomClaims:Map<string,obj> }

module UserIdentity =
    let private standardNames = Set.ofList [
        "tokenIdentifier";"subject";"issuer";"name";"givenName";"familyName"
        "nickname";"preferredUsername";"profileUrl";"pictureUrl";"email"
        "emailVerified";"gender";"birthday";"timezone";"language";"phoneNumber"
        "phoneNumberVerified";"address";"updatedAt" ]

    /// Snapshot provider data once. Malformed standard claims are not coerced.
    let decode (value:obj) =
        let raw=RawValueCodec.normalize value
        if not (NativeValue.isPlainObject raw) then invalidArg "value" "Expected an authenticated identity object"
        let required name =
            let value=NativeValue.getProperty raw name
            if NativeValue.jsTypeOf value<>"string" then invalidArg "value" ("Missing or invalid identity claim: "+name)
            unbox<string> value
        let optional name decode =
            let value=NativeValue.getProperty raw name
            if NativeValue.isUndefined value || NativeValue.isNull value then None else Some(decode value)
        let stringClaim name = optional name Codec.decode<string>
        let boolClaim name = optional name Codec.decode<bool>
        { TokenIdentifier=required "tokenIdentifier";Subject=required "subject";Issuer=required "issuer"
          Name=stringClaim "name";GivenName=stringClaim "givenName";FamilyName=stringClaim "familyName"
          Nickname=stringClaim "nickname";PreferredUsername=stringClaim "preferredUsername"
          ProfileUrl=stringClaim "profileUrl";PictureUrl=stringClaim "pictureUrl"
          Email=stringClaim "email";EmailVerified=boolClaim "emailVerified"
          Gender=stringClaim "gender";Birthday=stringClaim "birthday";Timezone=stringClaim "timezone";Language=stringClaim "language"
          PhoneNumber=stringClaim "phoneNumber";PhoneNumberVerified=boolClaim "phoneNumberVerified"
          Address=stringClaim "address";UpdatedAt=stringClaim "updatedAt"
          CustomClaims=NativeValue.objectKeys raw
                       |> Array.filter (fun key -> not (standardNames.Contains key))
                       |> Array.map (fun key -> key,NativeValue.getProperty raw key)
                       |> Map.ofArray }

    let inline claim<'T> name (identity:UserIdentity) : 'T option =
        identity.CustomClaims |> Map.tryFind name |> Option.map Codec.decode<'T>

type IAuth =
    abstract GetUserIdentity: unit -> Async<UserIdentity option>
