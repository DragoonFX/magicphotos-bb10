import QtQuick 1.0
import QtMobility.gallery 1.1
import com.nokia.meego 1.0

Page {
    id:           fileSavePage
    anchors.fill: parent

    property int fileNameRectangleHeight: 48
    property string fileUrlPath:          ""

    signal fileSelected(string fileUrl)
    signal saveCancelled()

    onStatusChanged: {
        if (status === PageStatus.Activating) {
            imageGridView.visible = false;
            waitRectangle.visible = true;
        } else if (status === PageStatus.Active) {
            imageGridView.currentIndex = -1;

            documentGalleryModel.reload();
        }
    }

    // MeeGo Only
    function utf8Decode(string) {
        var encoded_string = unescape(string);
        var decoded_string = "";
        var i              = 0;
        var c, c1, c2;

        while (i < encoded_string.length) {
            c = encoded_string.charCodeAt(i);

            if (c < 128) {
                decoded_string = decoded_string + String.fromCharCode(c);
                i              = i + 1;
            } else if ((c > 191) && (c < 224)) {
                c1             = encoded_string.charCodeAt(i + 1);
                decoded_string = decoded_string + String.fromCharCode(((c & 31) << 6) | (c1 & 63));
                i              = i + 2;
            } else {
                c1             = encoded_string.charCodeAt(i + 1);
                c2             = encoded_string.charCodeAt(i + 2);
                decoded_string = decoded_string + String.fromCharCode(((c & 15) << 12) | ((c1 & 63) << 6) | (c2 & 63));
                i              = i + 3;
            }
        }

        return decoded_string;
    }

    function setFileUrl(file_url) {
        var urlPathNameRegexp = /^(.+)\/([^/]+)$/;
        var urlPathNameArr;

        if ((urlPathNameArr = urlPathNameRegexp.exec(file_url)) !== null) {
            fileUrlPath            = urlPathNameArr[1];
            fileNameTextField.text = urlPathNameArr[2];
        } else {
            fileUrlPath            = "";
            fileNameTextField.text = "";
        }
    }

    function normalizeFileUrl(file_url) {
        var fileUrlRegexp = /^file:\/\/\/.+$/;

        if (fileUrlRegexp.exec(file_url) !== null) {
            return file_url;
        } else {
            return "file:///" + file_url;
        }
    }

    Rectangle {
        id:             gridViewBackground
        anchors.top:    parent.top
        anchors.bottom: fileNameRectangle.top
        anchors.left:   parent.left
        anchors.right:  parent.right
        color:          "black"

        GridView {
            id:           imageGridView
            anchors.fill: parent
            cellWidth:    width  > height ? Math.floor(width  / 5) : Math.floor(width  / 3)
            cellHeight:   height > width  ? Math.floor(height / 5) : Math.floor(height / 3)
            model:        documentGalleryModel
            delegate:     documentGalleryDelegate
            visible:      false

            onCurrentIndexChanged: {
                if (currentIndex !== -1) {
                    fileSavePage.setFileUrl(fileSavePage.normalizeFileUrl(fileSavePage.utf8Decode(documentGalleryModel.property(imageGridView.currentIndex, "url"))));
                }
            }

            DocumentGalleryModel {
                id:             documentGalleryModel
                rootType:       DocumentGallery.Image
                autoUpdate:     true
                properties:     ["url"]
                sortProperties: ["-lastModified"]

                onStatusChanged: {
                    if (status == DocumentGalleryModel.Finished || status === DocumentGalleryModel.Idle) {
                        waitRectangle.visible = false;
                        imageGridView.visible = true;
                    }
                }
            }

            Component {
                id: documentGalleryDelegate

                Rectangle {
                    id:           galleryItemRectangle
                    width:        imageGridView.cellWidth  - border.width
                    height:       imageGridView.cellHeight - border.width
                    color:        "transparent"
                    border.color: GridView.isCurrentItem ? "white" : "steelblue"
                    border.width: 2

                    MouseArea {
                        id:           galleryItemMouseArea
                        anchors.fill: parent

                        onClicked: {
                            imageGridView.currentIndex = index;
                        }

                        Image {
                            id:               galleryItemImage
                            anchors.centerIn: parent
                            width:            parent.width  - galleryItemRectangle.border.width
                            height:           parent.height - galleryItemRectangle.border.width
                            source:           fileSavePage.utf8Decode(url)
                            sourceSize.width: width
                            asynchronous:     true
                            fillMode:         Image.PreserveAspectFit
                            smooth:           false
                        }
                    }
                }
            }
        }

        Rectangle {
            id:           waitRectangle
            anchors.fill: parent
            color:        "transparent"

            MouseArea {
                id:           waitRectangleMouseArea
                anchors.fill: parent

                Image {
                    id:                       waitBusyIndicatorImage
                    anchors.horizontalCenter: parent.horizontalCenter
                    anchors.verticalCenter:   parent.verticalCenter
                    source:                   "qrc:/resources/images/busy_indicator.png"

                    NumberAnimation on rotation {
                        running: waitRectangle.visible
                        from:    0
                        to:      360
                        loops:   Animation.Infinite
                    }
                }
            }
        }
    }

    Rectangle {
        id:             fileNameRectangle
        anchors.bottom: bottomToolBar.top
        anchors.left:   parent.left
        anchors.right:  parent.right
        height:         fileSavePage.fileNameRectangleHeight
        z:              1
        color:          "black"

        TextField {
            id:           fileNameTextField
            anchors.fill: parent
        }
    }

    ToolBar {
        id:             bottomToolBar
        anchors.bottom: parent.bottom
        z:              1

        tools: ToolBarLayout {
            ButtonRow {
                id:        bottomToolBarButtonRow
                exclusive: false

                ToolButton {
                    id:         saveToolButton
                    iconSource: "qrc:/resources/images/save.png"
                    flat:       true
                    enabled:    fileSavePage.fileUrlPath !== "" && fileNameTextField.text !== ""

                    onClicked: {
                        if (fileSavePage.fileUrlPath !== "" && fileNameTextField.text !== "") {
                            fileSavePage.fileSelected(fileSavePage.fileUrlPath + "/" + fileNameTextField.text);
                        }
                    }
                }

                ToolButton {
                    id:         saveCancelToolButton
                    iconSource: "qrc:/resources/images/cancel.png"
                    flat:       true

                    onClicked: {
                        fileSavePage.saveCancelled();
                    }
                }
            }
        }
    }
}