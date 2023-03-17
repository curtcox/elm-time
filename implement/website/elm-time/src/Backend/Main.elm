module Backend.Main exposing
    ( State
    , webServerMain
    )

import Base64
import Build
import Bytes.Encode
import FileTree
import Platform.WebServer
import Url


type alias State =
    {}


webServerMain : Platform.WebServer.WebServerConfig State
webServerMain =
    { init = ( {}, [] )
    , subscriptions = subscriptions
    }


subscriptions : State -> Platform.WebServer.Subscriptions State
subscriptions _ =
    { httpRequest = updateForHttpRequestEvent
    , posixTimeIsPast = Nothing
    }


updateForHttpRequestEvent : Platform.WebServer.HttpRequestEventStruct -> State -> ( State, Platform.WebServer.Commands State )
updateForHttpRequestEvent httpRequestEvent stateBefore =
    let
        bodyFromString =
            Bytes.Encode.string >> Bytes.Encode.encode >> Base64.fromBytes

        staticContentHttpHeaders { contentType, contentEncoding } =
            { cacheMaxAgeMinutes = Just (60 * 24)
            , contentType = contentType
            , contentEncoding = contentEncoding
            }

        httpResponseOkWithStringContent stringContent httpResponseHeaders =
            httpResponseOkWithBodyAsBase64 (bodyFromString stringContent) httpResponseHeaders

        httpResponseOkWithBodyAsBase64 bodyAsBase64 contentConfig =
            { statusCode = 200
            , bodyAsBase64 = bodyAsBase64
            , headersToAdd =
                [ ( "Cache-Control"
                  , contentConfig.cacheMaxAgeMinutes
                        |> Maybe.map (\maxAgeMinutes -> "public, max-age=" ++ String.fromInt (maxAgeMinutes * 60))
                  )
                , ( "Content-Type", Just contentConfig.contentType )
                , ( "Content-Encoding", contentConfig.contentEncoding )
                ]
                    |> List.concatMap
                        (\( name, maybeValue ) ->
                            maybeValue
                                |> Maybe.map (\value -> [ { name = name, values = [ value ] } ])
                                |> Maybe.withDefault []
                        )
            }

        frontendHtmlDocumentResponse frontendConfig =
            httpResponseOkWithStringContent (Build.frontendHtmlDocument frontendConfig)
                (staticContentHttpHeaders { contentType = "text/html", contentEncoding = Nothing })

        cachedResponse bodyAsBase64 =
            { statusCode = 200
            , bodyAsBase64 = bodyAsBase64
            , headersToAdd =
                [ { name = "Cache-Control", values = [ "public, max-age=3600" ] }
                ]
            }

        response =
            if (httpRequestEvent.request.method |> String.toLower) /= "get" then
                { statusCode = 405
                , bodyAsBase64 = Nothing
                , headersToAdd = []
                }

            else
                case Url.fromString httpRequestEvent.request.uri of
                    Nothing ->
                        { statusCode = 500
                        , bodyAsBase64 =
                            "Failed to parse URL"
                                |> Bytes.Encode.string
                                |> Bytes.Encode.encode
                                |> Base64.fromBytes
                        , headersToAdd = []
                        }

                    Just url ->
                        case
                            Build.fileTree
                                |> FileTree.flatListOfBlobsFromFileTreeNode
                                |> List.filter (Tuple.first >> (==) (String.split "/" url.path |> List.filter (String.isEmpty >> not)))
                                |> List.head
                        of
                            Just ( _, matchingFile ) ->
                                matchingFile.base64
                                    |> Just
                                    |> cachedResponse

                            Nothing ->
                                frontendHtmlDocumentResponse { debug = False }

        httpResponse =
            { httpRequestId = httpRequestEvent.httpRequestId
            , response = response
            }
    in
    ( stateBefore
    , [ Platform.WebServer.RespondToHttpRequest httpResponse ]
    )
